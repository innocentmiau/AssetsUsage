using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AssetsUsage
{

    // Runs entirely on background threads once RunAsync is called. It never touches the
    // UnityEditor or UnityEngine API, which is what keeps it safe to run off the main thread.
    // Progress fields are volatile so the editor window can poll them from the main thread.
    public class AssetUsageScanner
    {

        // Unity GUIDs are 32 lowercase hex characters. The lookarounds stop us matching a 32 char
        // slice inside a longer hash. We keep only matches that are real asset GUIDs, which also
        // makes this work for YAML, JSON graph files and anything else that embeds a GUID.
        private static readonly Regex GUID_REGEX =
            new Regex("(?<![0-9a-fA-F])[0-9a-f]{32}(?![0-9a-fA-F])", RegexOptions.Compiled);

        private static readonly Regex IDENTIFIER_REGEX =
            new Regex("[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

        // Text serialized assets that can contain references to other assets.
        private static readonly HashSet<string> TEXT_REFERENCE_EXTENSIONS = new HashSet<string>
        {
            ".unity", ".prefab", ".mat", ".asset", ".controller", ".overridecontroller", ".anim",
            ".playable", ".mask", ".spriteatlas", ".spriteatlasv2", ".mixer", ".preset", ".rendertexture",
            ".terrainlayer", ".guiskin", ".shadervariants", ".signal", ".lighting", ".giparams", ".brush",
            ".physicmaterial", ".physicsmaterial2d", ".flare", ".cubemap", ".shadergraph", ".shadersubgraph", ".vfx"
        };

        private volatile float _progress;
        public float Progress => _progress;

        private volatile string _phaseLabel = "";
        public string PhaseLabel => _phaseLabel;

        private volatile bool _isRunning;
        public bool IsRunning => _isRunning;

        private volatile string _error;
        public string Error => _error;

        private List<AssetUsageResult> _results;
        public List<AssetUsageResult> Results => _results;

        private CancellationTokenSource _cancelSource;

        public Task RunAsync(ScanInput input)
        {

            _cancelSource = new CancellationTokenSource();
            _isRunning = true;
            _error = null;
            _progress = 0f;
            _results = null;

            CancellationToken token = _cancelSource.Token;
            return Task.Run(() => Execute(input, token));
        }

        public void Cancel() => _cancelSource?.Cancel();

        private void Execute(ScanInput input, CancellationToken token)
        {

            try
            {
                HashSet<string> knownGuids = new HashSet<string>(input.PathToGuid.Values);
                Dictionary<string, RefAccumulator> references = ScanReferences(input, knownGuids, token);

                Dictionary<string, HashSet<string>> scriptUsers = input.ScanScriptTypeReferences
                    ? BuildScriptReferenceIndex(input, token)
                    : new Dictionary<string, HashSet<string>>();

                _results = BuildResults(input, references, scriptUsers, token);
                _progress = 1f;
            }
            catch (OperationCanceledException)
            {
                _error = "Scan cancelled.";
            }
            catch (Exception exception)
            {
                _error = exception.Message;
            }
            finally
            {
                _isRunning = false;
            }
        }

        // Reads every asset file plus its meta, plus ProjectSettings, and tallies which asset
        // references which GUID. Runs in parallel with a per thread dictionary that is merged at the end.
        private Dictionary<string, RefAccumulator> ScanReferences(ScanInput input, HashSet<string> knownGuids, CancellationToken token)
        {

            _phaseLabel = "Scanning references";

            List<string> sources = input.PathToGuid.Keys.ToList();
            int total = sources.Count;
            int processed = 0;

            object mergeLock = new object();
            Dictionary<string, RefAccumulator> global = new Dictionary<string, RefAccumulator>();

            ParallelOptions options = new ParallelOptions
            {
                CancellationToken = token,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1)
            };

            Parallel.ForEach(
                sources,
                options,
                () => new Dictionary<string, RefAccumulator>(),
                (assetPath, loopState, local) =>
                {
                    ProcessSource(input, assetPath, input.PathToGuid[assetPath], knownGuids, local);

                    int done = Interlocked.Increment(ref processed);
                    if ((done & 63) == 0)
                        _progress = .8f * (done / (float)total);

                    return local;
                },
                local =>
                {
                    lock (mergeLock)
                        MergeInto(global, local);
                });

            ScanProjectSettings(input, knownGuids, global, token);
            return global;
        }

        private void ProcessSource(ScanInput input, string assetPath, string ownGuid, HashSet<string> knownGuids, Dictionary<string, RefAccumulator> local)
        {

            string absolute = Path.Combine(input.ProjectRoot, assetPath);
            Dictionary<string, int> occurrences = new Dictionary<string, int>();

            string extension = Path.GetExtension(assetPath).ToLowerInvariant();
            if (TEXT_REFERENCE_EXTENSIONS.Contains(extension))
                CollectGuids(ReadTextSafe(absolute), knownGuids, ownGuid, occurrences);

            CollectGuids(ReadTextSafe(absolute + ".meta"), knownGuids, ownGuid, occurrences);

            foreach (KeyValuePair<string, int> entry in occurrences)
                Record(local, entry.Key, assetPath, entry.Value);
        }

        private void ScanProjectSettings(ScanInput input, HashSet<string> knownGuids, Dictionary<string, RefAccumulator> global, CancellationToken token)
        {

            string settingsDir = Path.Combine(input.ProjectRoot, "ProjectSettings");
            if (!Directory.Exists(settingsDir)) return;

            foreach (string file in Directory.GetFiles(settingsDir, "*.asset"))
            {
                token.ThrowIfCancellationRequested();

                Dictionary<string, int> occurrences = new Dictionary<string, int>();
                CollectGuids(ReadTextSafe(file), knownGuids, null, occurrences);

                string label = "ProjectSettings/" + Path.GetFileName(file);
                foreach (KeyValuePair<string, int> entry in occurrences)
                    Record(global, entry.Key, label, entry.Value);
            }
        }

        // Heuristic only. GUID scanning cannot see one script referencing another by type name,
        // so this indexes every identifier in every .cs file and maps a script GUID to the files
        // that mention its class name. It can produce false positives on common names.
        private Dictionary<string, HashSet<string>> BuildScriptReferenceIndex(ScanInput input, CancellationToken token)
        {

            _phaseLabel = "Analyzing C# type references";
            _progress = .8f;

            List<string> scriptPaths = input.PathToGuid.Keys
                .Where(path => Path.GetExtension(path).ToLowerInvariant() == ".cs")
                .ToList();

            object indexLock = new object();
            Dictionary<string, HashSet<string>> identifierToFiles = new Dictionary<string, HashSet<string>>();

            ParallelOptions options = new ParallelOptions
            {
                CancellationToken = token,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1)
            };

            Parallel.ForEach(scriptPaths, options, scriptPath =>
            {
                string content = ReadTextSafe(Path.Combine(input.ProjectRoot, scriptPath));
                if (string.IsNullOrEmpty(content)) return;

                HashSet<string> identifiers = new HashSet<string>();
                foreach (Match match in IDENTIFIER_REGEX.Matches(content))
                    identifiers.Add(match.Value);

                lock (indexLock)
                {
                    foreach (string identifier in identifiers)
                    {
                        if (!identifierToFiles.TryGetValue(identifier, out HashSet<string> files))
                        {
                            files = new HashSet<string>();
                            identifierToFiles[identifier] = files;
                        }

                        files.Add(scriptPath);
                    }
                }
            });

            Dictionary<string, HashSet<string>> usersByGuid = new Dictionary<string, HashSet<string>>();
            foreach (string scriptPath in scriptPaths)
            {
                string className = Path.GetFileNameWithoutExtension(scriptPath);
                if (!identifierToFiles.TryGetValue(className, out HashSet<string> files)) continue;

                HashSet<string> others = new HashSet<string>(files);
                others.Remove(scriptPath);
                if (others.Count > 0)
                    usersByGuid[input.PathToGuid[scriptPath]] = others;
            }

            return usersByGuid;
        }

        private List<AssetUsageResult> BuildResults(ScanInput input, Dictionary<string, RefAccumulator> references, Dictionary<string, HashSet<string>> scriptUsers, CancellationToken token)
        {

            _phaseLabel = "Building results";
            _progress = .9f;

            List<AssetUsageResult> results = new List<AssetUsageResult>();
            bool wholeProject = (input.Folders?.Count ?? 0) == 0 || input.Folders.Contains("Assets");

            foreach (KeyValuePair<string, string> pair in input.PathToGuid)
            {
                token.ThrowIfCancellationRequested();

                string assetPath = pair.Key;
                if (!PassesFolderFilter(assetPath, input.Folders, wholeProject)) continue;
                if (Directory.Exists(Path.Combine(input.ProjectRoot, assetPath))) continue;

                AssetCategory category = AssetClassifier.Classify(assetPath);
                if ((input.CategoryMask & category) == 0) continue;

                string guid = pair.Value;
                AssetUsageResult result = new AssetUsageResult(assetPath, guid, category, AssetClassifier.IsRuntimeLoadable(assetPath));

                if (references.TryGetValue(guid, out RefAccumulator accumulator))
                {
                    foreach (string referencer in accumulator.Referencers)
                        result.AddReferencer(referencer);

                    result.AddOccurrences(accumulator.Occurrences);
                }

                if (input.BuildSettingsSceneGuids.Contains(guid))
                    result.AddReferencer("Build Settings");

                if (scriptUsers.TryGetValue(guid, out HashSet<string> typeUsers))
                {
                    foreach (string user in typeUsers)
                        result.AddReferencer(user);

                    result.AddOccurrences(typeUsers.Count);
                }

                results.Add(result);
            }

            return results;
        }

        private static bool PassesFolderFilter(string assetPath, List<string> folders, bool wholeProject)
        {

            if (wholeProject)
                return assetPath.StartsWith("Assets/") || assetPath == "Assets";

            foreach (string folder in folders)
            {
                if (assetPath == folder || assetPath.StartsWith(folder + "/"))
                    return true;
            }

            return false;
        }

        private static void CollectGuids(string content, HashSet<string> knownGuids, string ownGuid, Dictionary<string, int> into)
        {

            if (string.IsNullOrEmpty(content)) return;

            foreach (Match match in GUID_REGEX.Matches(content))
            {
                string guid = match.Value;
                if (guid == ownGuid) continue;
                if (!knownGuids.Contains(guid)) continue;

                into.TryGetValue(guid, out int existing);
                into[guid] = existing + 1;
            }
        }

        private static void Record(Dictionary<string, RefAccumulator> map, string referencedGuid, string referencingPath, int occurrences)
        {

            if (!map.TryGetValue(referencedGuid, out RefAccumulator accumulator))
            {
                accumulator = new RefAccumulator();
                map[referencedGuid] = accumulator;
            }

            accumulator.Occurrences += occurrences;
            accumulator.Referencers.Add(referencingPath);
        }

        private static void MergeInto(Dictionary<string, RefAccumulator> target, Dictionary<string, RefAccumulator> source)
        {

            foreach (KeyValuePair<string, RefAccumulator> entry in source)
            {
                if (!target.TryGetValue(entry.Key, out RefAccumulator accumulator))
                {
                    accumulator = new RefAccumulator();
                    target[entry.Key] = accumulator;
                }

                accumulator.Occurrences += entry.Value.Occurrences;
                accumulator.Referencers.UnionWith(entry.Value.Referencers);
            }
        }

        private static string ReadTextSafe(string absolutePath)
        {

            try
            {
                return File.Exists(absolutePath) ? File.ReadAllText(absolutePath) : null;
            }
            catch
            {
                return null;
            }
        }

        private class RefAccumulator
        {

            public int Occurrences;
            public HashSet<string> Referencers = new HashSet<string>();
        }
    }
}
