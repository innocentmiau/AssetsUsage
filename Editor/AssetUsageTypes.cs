using System;
using System.Collections.Generic;
using System.IO;

namespace AssetsUsage
{

    // Bit flags so the window can use a single mask field and a multi select popup.
    [Flags]
    public enum AssetCategory
    {
        NONE = 0,
        SCRIPT = 1 << 0,
        MATERIAL = 1 << 1,
        SHADER = 1 << 2,
        MODEL = 1 << 3,
        TEXTURE = 1 << 4,
        AUDIO = 1 << 5,
        ANIMATION = 1 << 6,
        PREFAB = 1 << 7,
        SCENE = 1 << 8,
        SCRIPTABLE_OBJECT = 1 << 9,
        FONT = 1 << 10,
        OTHER = 1 << 11,
        ALL = ~0
    }

    public enum SortMode
    {
        LEAST_USED_FIRST, MOST_USED_FIRST, NAME, CATEGORY
    }

    // Everything the background scanner needs, gathered on the main thread before the scan starts.
    public class ScanInput
    {

        public string ProjectRoot;
        public Dictionary<string, string> PathToGuid;
        public HashSet<string> BuildSettingsSceneGuids;
        public List<string> Folders;
        public AssetCategory CategoryMask;
        public bool ScanScriptTypeReferences;
    }

    // Usage record for a single asset. Referencers are stored as a set so the same
    // asset referencing a target twice still counts as one user, while occurrences track raw hits.
    public class AssetUsageResult
    {

        public string AssetPath { get; private set; }
        public string Guid { get; private set; }
        public AssetCategory Category { get; private set; }
        public bool RuntimeLoadable { get; private set; }
        public int ReferenceOccurrences { get; private set; }

        private readonly HashSet<string> _referencingPaths = new HashSet<string>();
        public IReadOnlyCollection<string> ReferencingPaths => _referencingPaths;
        public int UsedByCount => _referencingPaths.Count;

        public AssetUsageResult(string assetPath, string guid, AssetCategory category, bool runtimeLoadable)
        {

            AssetPath = assetPath;
            Guid = guid;
            Category = category;
            RuntimeLoadable = runtimeLoadable;
        }

        public void AddReferencer(string referencingPath) => _referencingPaths.Add(referencingPath);

        public void AddOccurrences(int count) => ReferenceOccurrences += count;
    }

    // Pure helper - no Unity API calls, so it is safe to use from background threads.
    public static class AssetClassifier
    {

        private static readonly HashSet<string> SCRIPT_EXTENSIONS = new HashSet<string> { ".cs" };

        private static readonly HashSet<string> MATERIAL_EXTENSIONS = new HashSet<string> { ".mat" };

        private static readonly HashSet<string> SHADER_EXTENSIONS = new HashSet<string>
        {
            ".shader", ".shadergraph", ".shadersubgraph", ".compute", ".cginc", ".hlsl", ".hlslinc", ".glslinc", ".shadervariants"
        };

        private static readonly HashSet<string> MODEL_EXTENSIONS = new HashSet<string>
        {
            ".fbx", ".obj", ".blend", ".dae", ".3ds", ".max", ".ma", ".mb", ".c4d", ".gltf", ".glb"
        };

        private static readonly HashSet<string> TEXTURE_EXTENSIONS = new HashSet<string>
        {
            ".png", ".jpg", ".jpeg", ".tga", ".psd", ".tif", ".tiff", ".exr", ".bmp", ".gif", ".hdr", ".webp", ".iff", ".pict"
        };

        private static readonly HashSet<string> AUDIO_EXTENSIONS = new HashSet<string>
        {
            ".wav", ".mp3", ".ogg", ".aiff", ".aif", ".flac", ".mod", ".it", ".s3m", ".xm"
        };

        private static readonly HashSet<string> ANIMATION_EXTENSIONS = new HashSet<string>
        {
            ".anim", ".controller", ".overridecontroller", ".playable", ".mask"
        };

        private static readonly HashSet<string> PREFAB_EXTENSIONS = new HashSet<string> { ".prefab" };

        private static readonly HashSet<string> SCENE_EXTENSIONS = new HashSet<string> { ".unity" };

        private static readonly HashSet<string> FONT_EXTENSIONS = new HashSet<string> { ".ttf", ".otf", ".fontsettings", ".fon" };

        public static AssetCategory Classify(string assetPath)
        {

            string ext = Path.GetExtension(assetPath).ToLowerInvariant();

            if (SCRIPT_EXTENSIONS.Contains(ext)) return AssetCategory.SCRIPT;
            if (MATERIAL_EXTENSIONS.Contains(ext)) return AssetCategory.MATERIAL;
            if (SHADER_EXTENSIONS.Contains(ext)) return AssetCategory.SHADER;
            if (MODEL_EXTENSIONS.Contains(ext)) return AssetCategory.MODEL;
            if (TEXTURE_EXTENSIONS.Contains(ext)) return AssetCategory.TEXTURE;
            if (AUDIO_EXTENSIONS.Contains(ext)) return AssetCategory.AUDIO;
            if (ANIMATION_EXTENSIONS.Contains(ext)) return AssetCategory.ANIMATION;
            if (PREFAB_EXTENSIONS.Contains(ext)) return AssetCategory.PREFAB;
            if (SCENE_EXTENSIONS.Contains(ext)) return AssetCategory.SCENE;
            if (FONT_EXTENSIONS.Contains(ext)) return AssetCategory.FONT;
            if (ext == ".asset") return AssetCategory.SCRIPTABLE_OBJECT;

            return AssetCategory.OTHER;
        }

        // Assets under Resources or StreamingAssets can be loaded by name at runtime,
        // so a zero reference count does not prove they are unused.
        public static bool IsRuntimeLoadable(string assetPath)
        {

            string normalized = assetPath.Replace('\\', '/');
            return normalized.Contains("/Resources/") || normalized.Contains("/StreamingAssets/");
        }
    }
}
