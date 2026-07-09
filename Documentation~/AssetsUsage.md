# Assets Usage

Assets Usage is an editor-only tool that scans your Unity project and reports how
many times each asset is referenced. Assets with the fewest references bubble to
the top, making unused or rarely used assets easy to spot and clean up.

## How it works

The scanner reference-counts assets by their GUID. It reads the raw text of your
scenes, prefabs, materials, ScriptableObjects and `.meta` files, and tallies how
often each asset's GUID appears across the project. File scanning runs on
background threads so the editor stays responsive during large scans.

## Using Assets Usage

1. Open the window from **Tools > Assets Usage**.
2. (Optional) Set a **folder filter** to restrict the scan to part of your project.
3. (Optional) Set an **asset-type filter** to only report on a specific kind of asset.
4. Run the scan and let it finish (progress runs on background threads).
5. Sort results by **least used** or **most used**.
6. Use **Export CSV** to save the report for review outside the editor.

## Reading the results

Each row shows an asset and its reference count. A count of `0` means no other
asset in the scanned scope references it — a strong candidate for removal. Always
verify before deleting, as assets can be referenced dynamically at runtime (for
example, loaded by name via `Resources.Load` or Addressables), which GUID-based
scanning cannot detect.
