using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace AssetsUsage
{

    public class AssetUsageFinderWindow : EditorWindow
    {

        private const float ROW_HEIGHT = 20f;
        private const float ICON_SIZE = 16f;

        [SerializeField] private List<string> folders = new List<string> { "Assets" };
        [SerializeField] private AssetCategory categoryMask = AssetCategory.ALL;
        [SerializeField] private bool scanScriptTypeReferences = false;
        [SerializeField] private SortMode sortMode = SortMode.LEAST_USED_FIRST;
        [SerializeField] private bool hideUsed = false;
        [SerializeField] private bool hideRuntimeLoadable = false;
        [SerializeField] private string searchFilter = "";

        private readonly AssetUsageScanner _scanner = new AssetUsageScanner();
        private bool _pendingFinalize;
        private List<AssetUsageResult> _results = new List<AssetUsageResult>();
        private readonly HashSet<string> _expanded = new HashSet<string>();
        private Object _folderPickerObject;
        private Vector2 _scroll;
        private string _summary = "";

        [MenuItem("Tools/Assets Usage")]
        private static void Open()
        {
            AssetUsageFinderWindow window = GetWindow<AssetUsageFinderWindow>("Assets Usage");
            window.minSize = new Vector2(560f, 400f);
            window.Show();
        }

        private void OnEnable() => EditorApplication.update += OnEditorUpdate;

        private void OnDisable() => EditorApplication.update -= OnEditorUpdate;

        // Repaints while a scan is running and finalizes results once the background task ends.
        private void OnEditorUpdate()
        {

            if (!_pendingFinalize) return;

            if (_scanner.IsRunning)
            {
                Repaint();
                return;
            }

            _pendingFinalize = false;
            _results = _scanner.Results ?? new List<AssetUsageResult>();
            _expanded.Clear();
            BuildSummary();
            Repaint();
        }

        private void OnGUI()
        {

            DrawScopeSection();
            DrawFilterSection();
            DrawActionsSection();
            EditorGUILayout.Space();
            DrawResultsSection();
        }

        private void DrawScopeSection()
        {

            EditorGUILayout.LabelField("Folders to scan", EditorStyles.boldLabel);

            for (int i = 0; i < folders.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(folders[i]);
                if (GUILayout.Button("Remove", GUILayout.Width(70f)))
                {
                    folders.RemoveAt(i);
                    GUIUtility.ExitGUI();
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.BeginHorizontal();
            _folderPickerObject = EditorGUILayout.ObjectField("Add folder", _folderPickerObject, typeof(DefaultAsset), false);
            if (_folderPickerObject)
            {
                string path = AssetDatabase.GetAssetPath(_folderPickerObject);
                if (AssetDatabase.IsValidFolder(path) && !folders.Contains(path))
                    folders.Add(path);

                _folderPickerObject = null;
            }

            if (GUILayout.Button("Whole project", GUILayout.Width(110f)))
            {
                folders.Clear();
                folders.Add("Assets");
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawFilterSection()
        {

            EditorGUILayout.Space();
            categoryMask = (AssetCategory)EditorGUILayout.EnumFlagsField("Asset types", categoryMask);
            scanScriptTypeReferences = EditorGUILayout.ToggleLeft(
                "Scan C# type references (heuristic, slower - catches script to script usage)",
                scanScriptTypeReferences);
        }

        private void DrawActionsSection()
        {

            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();

            EditorGUI.BeginDisabledGroup(_scanner.IsRunning);
            if (GUILayout.Button("Scan", GUILayout.Height(26f)))
                StartScan();

            EditorGUI.EndDisabledGroup();

            EditorGUI.BeginDisabledGroup(!_scanner.IsRunning);
            if (GUILayout.Button("Cancel", GUILayout.Height(26f), GUILayout.Width(90f)))
                _scanner.Cancel();

            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            if (_scanner.IsRunning)
            {
                Rect rect = EditorGUILayout.GetControlRect(false, 20f);
                EditorGUI.ProgressBar(rect, _scanner.Progress, _scanner.PhaseLabel + " " + Mathf.RoundToInt(_scanner.Progress * 100f) + "%");
            }

            if (!string.IsNullOrEmpty(_scanner.Error))
                EditorGUILayout.HelpBox(_scanner.Error, MessageType.Warning);
        }

        private void DrawResultsSection()
        {

            if (_results.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No results yet. Choose folders and types, then press Scan.\n\n" +
                    "Note: assets loaded via Resources, Addressables or Asset Bundles may show as unused even when used at runtime.",
                    MessageType.Info);
                return;
            }

            DrawResultsToolbar();

            List<AssetUsageResult> view = GetFilteredSorted();
            EditorGUILayout.LabelField(_summary + "   Showing " + view.Count, EditorStyles.miniBoldLabel);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (AssetUsageResult result in view)
                DrawResultRow(result);

            EditorGUILayout.EndScrollView();
        }

        private void DrawResultsToolbar()
        {

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            sortMode = (SortMode)EditorGUILayout.EnumPopup(sortMode, EditorStyles.toolbarPopup, GUILayout.Width(160f));
            hideUsed = GUILayout.Toggle(hideUsed, "Unused only", EditorStyles.toolbarButton, GUILayout.Width(90f));
            hideRuntimeLoadable = GUILayout.Toggle(hideRuntimeLoadable, "Hide runtime loadable", EditorStyles.toolbarButton, GUILayout.Width(150f));
            searchFilter = GUILayout.TextField(searchFilter, EditorStyles.toolbarSearchField);

            if (GUILayout.Button("Select unused", EditorStyles.toolbarButton, GUILayout.Width(100f)))
                SelectUnusedInProject();

            if (GUILayout.Button("Export CSV", EditorStyles.toolbarButton, GUILayout.Width(90f)))
                ExportCsv();

            EditorGUILayout.EndHorizontal();
        }

        private void DrawResultRow(AssetUsageResult result)
        {

            EditorGUILayout.BeginHorizontal();

            bool expanded = _expanded.Contains(result.AssetPath);
            bool toggled = EditorGUILayout.Foldout(expanded, GUIContent.none, true, EditorStyles.foldout);
            if (toggled != expanded)
                SetExpanded(result.AssetPath, toggled);

            Texture icon = AssetDatabase.GetCachedIcon(result.AssetPath);
            if (icon)
                GUILayout.Label(icon, GUILayout.Width(ICON_SIZE), GUILayout.Height(ICON_SIZE));

            Color previous = GUI.color;
            if (result.UsedByCount == 0)
                GUI.color = result.RuntimeLoadable ? new Color(1f, .8f, .35f) : new Color(1f, .5f, .5f);

            if (GUILayout.Button(Path.GetFileName(result.AssetPath), EditorStyles.label, GUILayout.MinWidth(160f)))
                PingAsset(result.AssetPath);

            GUI.color = previous;

            GUILayout.FlexibleSpace();
            GUILayout.Label(result.Category.ToString(), GUILayout.Width(140f));
            GUILayout.Label("used by " + result.UsedByCount + " (" + result.ReferenceOccurrences + " refs)", GUILayout.Width(160f));
            EditorGUILayout.EndHorizontal();

            if (_expanded.Contains(result.AssetPath))
                DrawReferencers(result);
        }

        private void DrawReferencers(AssetUsageResult result)
        {

            EditorGUI.indentLevel += 2;

            if (result.UsedByCount == 0)
                EditorGUILayout.LabelField("Not referenced anywhere in scanned sources.", EditorStyles.miniLabel);

            foreach (string referencer in result.ReferencingPaths.OrderBy(path => path))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(referencer, EditorStyles.miniLabel))
                    PingAsset(referencer);

                EditorGUILayout.EndHorizontal();
            }

            EditorGUI.indentLevel -= 2;
        }

        private void StartScan()
        {

            string[] allPaths = AssetDatabase.GetAllAssetPaths();
            Dictionary<string, string> pathToGuid = new Dictionary<string, string>(allPaths.Length);
            foreach (string path in allPaths)
            {
                if (!path.StartsWith("Assets") && !path.StartsWith("Packages")) continue;

                string guid = AssetDatabase.AssetPathToGUID(path);
                if (!string.IsNullOrEmpty(guid))
                    pathToGuid[path] = guid;
            }

            HashSet<string> buildScenes = new HashSet<string>();
            foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
            {
                if (scene.enabled)
                    buildScenes.Add(scene.guid.ToString());
            }

            ScanInput input = new ScanInput
            {
                ProjectRoot = Directory.GetParent(Application.dataPath).FullName,
                PathToGuid = pathToGuid,
                BuildSettingsSceneGuids = buildScenes,
                Folders = new List<string>(folders),
                CategoryMask = categoryMask,
                ScanScriptTypeReferences = scanScriptTypeReferences
            };

            _results = new List<AssetUsageResult>();
            _summary = "";
            _pendingFinalize = true;
            _scanner.RunAsync(input);
        }

        private List<AssetUsageResult> GetFilteredSorted()
        {

            IEnumerable<AssetUsageResult> query = _results;

            if (hideUsed)
                query = query.Where(result => result.UsedByCount == 0);

            if (hideRuntimeLoadable)
                query = query.Where(result => !result.RuntimeLoadable);

            if (!string.IsNullOrEmpty(searchFilter))
                query = query.Where(result => result.AssetPath.IndexOf(searchFilter, System.StringComparison.OrdinalIgnoreCase) >= 0);

            switch (sortMode)
            {
                case SortMode.LEAST_USED_FIRST:
                    query = query.OrderBy(result => result.UsedByCount).ThenBy(result => result.AssetPath);
                    break;
                case SortMode.MOST_USED_FIRST:
                    query = query.OrderByDescending(result => result.UsedByCount).ThenBy(result => result.AssetPath);
                    break;
                case SortMode.NAME:
                    query = query.OrderBy(result => result.AssetPath);
                    break;
                case SortMode.CATEGORY:
                    query = query.OrderBy(result => result.Category.ToString()).ThenBy(result => result.UsedByCount);
                    break;
            }

            return query.ToList();
        }

        private void SetExpanded(string assetPath, bool expanded)
        {

            if (expanded)
                _expanded.Add(assetPath);
            else
                _expanded.Remove(assetPath);
        }

        private void BuildSummary()
        {

            int unused = _results.Count(result => result.UsedByCount == 0);
            _summary = "Scanned " + _results.Count + " assets, " + unused + " unused.";
        }

        private static void PingAsset(string assetPath)
        {

            Object asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (!asset) return;

            EditorGUIUtility.PingObject(asset);
            Selection.activeObject = asset;
        }

        private void SelectUnusedInProject()
        {

            List<Object> unused = new List<Object>();
            foreach (AssetUsageResult result in GetFilteredSorted())
            {
                if (result.UsedByCount > 0) continue;

                Object asset = AssetDatabase.LoadMainAssetAtPath(result.AssetPath);
                if (asset)
                    unused.Add(asset);
            }

            Selection.objects = unused.ToArray();
        }

        private void ExportCsv()
        {

            string path = EditorUtility.SaveFilePanel("Export asset usage", "", "asset_usage.csv", "csv");
            if (string.IsNullOrEmpty(path)) return;

            StringBuilder builder = new StringBuilder();
            builder.AppendLine("AssetPath,Category,UsedByCount,ReferenceOccurrences,RuntimeLoadable");

            foreach (AssetUsageResult result in GetFilteredSorted())
            {
                builder.AppendLine(string.Join(",",
                    Escape(result.AssetPath),
                    result.Category.ToString(),
                    result.UsedByCount.ToString(),
                    result.ReferenceOccurrences.ToString(),
                    result.RuntimeLoadable.ToString()));
            }

            File.WriteAllText(path, builder.ToString());
            EditorUtility.RevealInFinder(path);
        }

        private static string Escape(string value) => value.Contains(",") ? "\"" + value + "\"" : value;
    }
}
