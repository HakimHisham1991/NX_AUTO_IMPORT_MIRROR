# NX_AUTO_IMPORT_MIRROR

![NX Version](https://img.shields.io/badge/Siemens%20NX-2512-blue)
![Language](https://img.shields.io/badge/Language-C%23-green)
![License](https://img.shields.io/badge/License-MIT-yellow)

A Siemens NX Open journal that batch-imports numbered Parasolid (`.x_t`) files, mirrors each one across a predefined datum plane, optimizes geometry, organizes bodies into groups and layers, and cleans up after itself — automatically.

---

## The Problem

Importing a set of Parasolid files, mirroring each one, assigning it to a layer, and tidying up the temporary geometry is a multi-step process that doesn't scale. This journal runs the entire sequence unattended, from file `2.x_t` to `255.x_t`, with a full log of what succeeded, what was skipped, and what failed.

---

## What It Does

For each `N.x_t` in the source folder:

1. Switch to Modeling application
2. Import the Parasolid file
3. Group imported bodies temporarily as `IMPORT_A`
4. Mirror across `MASTER_MIRROR_PLANE`
5. Optimize all faces (imported + mirrored) to the configured tolerance
6. Delete the temporary import group and its members
7. Create a final group named after the file index (`N`)
8. Move bodies to Layer `N` and hide the layer
9. Log the result

Layers that already contain objects are skipped automatically (idempotent).

---

## Requirements

- Siemens NX 2512 (tested)
- A work part open in Modeling or CAM environment
- A datum plane named exactly **`MASTER_MIRROR_PLANE`** on Layer 1, visible (not blanked)
- Numbered Parasolid files in the source folder:

```
C:\Users\Public\Documents\NX_AUTO_IMPORT_MIRROR\CAD\
├── 2.x_t
├── 3.x_t
├── ...
└── 255.x_t
```

---

## Usage

**1. Open your `.prt` file in NX.**

**2. Run the journal:**
> File → Execute → NX Open → select `BatchMirrorImport.cs`

The journal processes all files in the configured range and writes a summary to both the NX Listing Window and `batch_import.log`.

**Alternatively**, run the numbered sample journals individually for debugging or step-by-step control.

---

## Configuration

Edit these constants at the top of `BatchMirrorImport.cs`:

```csharp
private const string SourceFolder        = @"C:\Users\Public\Documents\NX_AUTO_IMPORT_MIRROR\CAD";
private const string MirrorPlaneName     = "MASTER_MIRROR_PLANE";
private const string ImportGroupName     = "IMPORT_A";
private const int    MinFileIndex        = 2;
private const int    MaxFileIndex        = 255;
private const double OptimizeToleranceMm = 0.010;
```

---

## Project Structure

```
NX_AUTO_IMPORT_MIRROR/
├── NX_AUTO_IMPORT_MIRROR/
│   ├── BatchMirrorImport.cs             ← Main journal (run this)
│   └── SAMPLE_CODE/
│       ├── 001_MODELING.cs              ← Switch to Modeling
│       ├── 002_IMPORT_PARASOLID.cs      ← Import a single .x_t file
│       ├── 003_GROUP_IMPORTED.cs        ← Group imported bodies
│       ├── 004_MIRROR.cs                ← Mirror across datum plane
│       ├── 005_OPTIMIZE.cs              ← Optimize Face on all bodies
│       ├── 006_DELETE_IMPORTED.cs       ← Delete temporary import group
│       ├── 007_GROUP_MIRRORED_BODIES.cs ← Create final numbered group
│       └── 008_LAYER_SETTING.cs         ← Move to layer and hide
├── LICENSE
└── README.md
```

The `SAMPLE_CODE` journals are standalone, single-step scripts — useful for learning the NXOpen workflow or isolating a specific operation for debugging.

---

## Logging

A `batch_import.log` file is written next to the source folder (or in Documents as fallback). It includes a timestamp, per-file results, skipped layers, and a final success/skip/failure summary. All output is also printed to the NX Listing Window.

---

## Troubleshooting

| Issue | Solution |
|---|---|
| `MASTER_MIRROR_PLANE` not found | Create or rename the datum plane on Layer 1 — name is case-sensitive |
| Layer not empty, file skipped | Clear objects on the target layer or adjust `MinFileIndex` |
| Import fails | Verify `.x_t` files are valid Parasolid exports from a compatible version |
| Mirror fails | Confirm the datum plane is properly defined and not suppressed |
| Testing a subset | Set `MaxFileIndex` to a lower value (e.g. `5`) |

---

## Contributing

Contributions welcome. Ideas for improvement:

- Support for additional import formats (STEP, IGES)
- GUI configuration dialog
- Progress bar and cancellation support
- Post-processing options (export, downstream CAM handoff)

Fork → feature branch → PR.

---

## Disclaimer

Test on non-production files first. Always back up your NX data. Verify mirrored geometry before downstream use. This software is provided as-is with no warranty.

---

## License

MIT — see [LICENSE](LICENSE) for details.

---

*Built for efficient CAD mirroring workflows in Siemens NX by [Hakim Hisham](https://github.com/HakimHisham1991).*
