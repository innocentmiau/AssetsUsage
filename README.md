# Assets Usage

Editor-only Unity tool that scans your project and reports how many times each
asset is referenced, so **unused assets surface at the top**. It reference-counts
assets by GUID across scenes, prefabs, materials, ScriptableObjects and `.meta`
files, and does the heavy file scanning on background threads to keep the editor
responsive.

- Folder and asset-type filters
- Sort by least or most used
- CSV export
- Background-threaded scanning

## Requirements

- Unity **6.3** (`6000.3`) or newer
- Editor-only: no runtime code or dependencies

## Installation (Git URL)

In Unity, open **Window > Package Manager**, click **+ > Add package from git URL…**,
and paste:

```
https://github.com/innocentmiau/AssetsUsage.git
```

To pin a specific release, append the tag:

```
https://github.com/innocentmiau/AssetsUsage.git#v1.0.0
```

Alternatively, add it directly to your project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.andreleandrodev.assetsusage": "https://github.com/innocentmiau/AssetsUsage.git#v1.0.0"
  }
}
```

## Usage

1. Open **Tools > Assets Usage**.
2. Optionally set a **folder** and/or **asset-type** filter to narrow the scan.
3. Run the scan and wait for it to finish.
4. Sort by **least used** to surface removal candidates, or **most used** to find hotspots.
5. Click **Export CSV** to save the report.

A reference count of `0` means nothing in the scanned scope points at that asset.
Verify before deleting: assets loaded dynamically at runtime (e.g. `Resources.Load`
or Addressables) cannot be detected by GUID scanning.

## License

[MIT](LICENSE.md) © Andre Leandro
