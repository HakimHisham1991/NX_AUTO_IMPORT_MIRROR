// NX 2512 - Batch Parasolid import, mirror, optimize, layer/group workflow
// Based on journals in SAMPLE_CODE/
//
// Run from NX: File > Execute > NX Open... (or Journal)
// Prerequisites: Work part open in CAM/Modeling with datum plane named exactly
//                MASTER_MIRROR_PLANE on layer 1, visible.
//
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NXOpen;
using NXOpen.Features;
using NXOpen.Layer;
using NXOpen.UF;

public class NXJournal
{
    private const string MirrorPlaneName = "MASTER_MIRROR_PLANE";
    private const string SourceFolder = @"C:\Users\Public\Documents\NX_AUTO_IMPORT_MIRROR\CAD";
    private const string ImportGroupName = "IMPORT_A";
    private const int MinFileIndex = 2;
    private const int MaxFileIndex = 255;
    private const double OptimizeToleranceMm = 0.010;

    private static Session _session;
    private static Part _workPart;
    private static UFSession _ufSession;
    private static readonly StringBuilder _log = new StringBuilder();
    private static string _logPath;

    public static void Main(string[] args)
    {
        _session = Session.GetSession();
        _workPart = _session.Parts.Work;
        _ufSession = UFSession.GetUFSession();

        if (_workPart == null)
        {
            Fail("No work part is open. Open the CAM part and run again.");
            return;
        }

        string logDir = Directory.Exists(SourceFolder)
            ? Path.GetDirectoryName(Path.GetFullPath(SourceFolder))
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        _logPath = Path.Combine(logDir, "batch_import.log");

        LogHeader();
        WriteLog("Work part: " + _workPart.Leaf);

        try
        {
            DatumPlane mirrorDatum = ValidateMirrorPlane();
            if (mirrorDatum == null)
                return;

            int processed = 0;
            int skipped = 0;
            int failed = 0;

            foreach (int index in GetFileIndices())
            {
                string filePath = Path.Combine(SourceFolder, index + ".x_t");
                string groupName = index.ToString();

                if (!File.Exists(filePath))
                {
                    LogSkip(index, "File not found: " + filePath);
                    skipped++;
                    continue;
                }

                if (!IsTargetLayerEmpty(index))
                {
                    LogSkip(index, "Layer " + index + " is not empty - skipped.");
                    skipped++;
                    continue;
                }

                try
                {
                    ProcessOneFile(filePath, groupName, index, mirrorDatum);
                    processed++;
                    WriteLog("OK: " + index + ".x_t");
                }
                catch (Exception ex)
                {
                    failed++;
                    LogError(index, ex.Message);
                }
            }

            LogNonProcessedSourceFiles();

            WriteLog(string.Format(
                "Finished. Processed={0}, Skipped={1}, Failed={2}",
                processed, skipped, failed));
            FlushLog();

            _session.ListingWindow.Open();
            _session.ListingWindow.WriteLine(_log.ToString());
            _session.ListingWindow.WriteLine("Log written to: " + _logPath);

            if (failed > 0)
                _ufSession.Ui.DisplayMessage(
                    "Batch import completed with errors. See batch_import.log.",
                    1);
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    // -------------------------------------------------------------------------
    // Per-file pipeline (steps 1–11)
    // -------------------------------------------------------------------------
    private static void ProcessOneFile(
        string xtPath,
        string groupName,
        int layerNumber,
        DatumPlane mirrorDatum)
    {
        // 1 - Application > Modeling (001_MODELING.cs)
        _session.ApplicationSwitchImmediate("UG_APP_MODELING");

        HashSet<Tag> bodiesBefore = SnapshotBodyTags();

        // 2 - Import Parasolid (002_IMPORT_PARASOLID.cs)
        ImportParasolid(xtPath);

        List<Body> importedBodies = GetNewSolidAndSheetBodies(bodiesBefore);
        if (importedBodies.Count == 0)
            throw new Exception("No solid or sheet bodies were imported.");

        // 3–4 - Group imported bodies as IMPORT_A (UF; UI group is not journaled)
        FeatureGroup importGroup = CreateFeatureGroup(
            ImportGroupName,
            GetBrepFeaturesFromBodies(importedBodies));

        // 5 - Mirror Geometry (004_MIRROR.cs)
        List<Body> mirroredBodies = MirrorBodies(importedBodies, mirrorDatum);

        // 6 - Optimize Face (005_OPTIMIZE.cs)
        List<Body> optimizeBodies = new List<Body>(importedBodies);
        optimizeBodies.AddRange(mirroredBodies);
        OptimizeFaces(optimizeBodies);

        // 7 - Delete IMPORT_A group and members (006_DELETE_IMPORTED.cs)
        DeleteFeatureGroupAndMembers(importGroup);

        // 8 - Group remaining mirrored bodies by file name (007_GROUP_MIRRORED_BODIES.cs)
        List<Body> finalBodies = GetNewSolidAndSheetBodies(bodiesBefore);
        if (finalBodies.Count == 0)
            throw new Exception("No mirrored bodies remain after deleting import group.");

        CreateFeatureGroup(
            groupName,
            GetBrepFeaturesFromBodies(finalBodies));

        // 9 - Move group bodies to target layer (008_LAYER_SETTING.cs uses DisplayModification)
        MoveBodiesToLayer(finalBodies, layerNumber);

        // 10 - Hide target layer (layer settings)
        HideLayer(layerNumber);

        _workPart.ModelingViews.WorkView.Fit();
        _session.UpdateManager.DoUpdate(_session.NewestVisibleUndoMark);
    }

    // -------------------------------------------------------------------------
    // Validation
    // -------------------------------------------------------------------------
    private static DatumPlane ValidateMirrorPlane()
    {
        DatumPlane plane = FindMirrorPlaneByName(MirrorPlaneName);
        if (plane == null)
        {
            Fail(
                "MASTER_MIRROR_PLANE not found (name is case-sensitive). " +
                "Create or rename the CAM datum plane and run again.");
            return null;
        }

        DisplayableObject disp = plane as DisplayableObject;
        if (disp != null)
        {
            if (disp.IsBlanked)
            {
                Fail("MASTER_MIRROR_PLANE exists but is blanked (not visible).");
                return null;
            }

            if (disp.Layer != 1)
            {
                Fail(
                    "MASTER_MIRROR_PLANE must be on layer 1 (current layer: " +
                    disp.Layer + ").");
                return null;
            }
        }

        if (!IsLayerVisible(1))
        {
            Fail("Layer 1 is hidden; MASTER_MIRROR_PLANE is not visible.");
            return null;
        }

        WriteLog("Validated MASTER_MIRROR_PLANE on layer 1.");
        return plane;
    }

    private static DatumPlane FindMirrorPlaneByName(string exactName)
    {
        foreach (NXObject datum in _workPart.Datums)
        {
            DatumPlane dp = datum as DatumPlane;
            if (dp != null && string.Equals(dp.Name, exactName, StringComparison.Ordinal))
                return dp;
        }

        foreach (Feature feat in _workPart.Features)
        {
            if (!string.Equals(feat.Name, exactName, StringComparison.Ordinal))
                continue;

            DatumPlane fromFeat = TryGetDatumPlaneFromFeature(feat);
            if (fromFeat != null)
                return fromFeat;
        }

        return null;
    }

    private static DatumPlane TryGetDatumPlaneFromFeature(Feature feat)
    {
        PropertyInfo prop = feat.GetType().GetProperty("DatumPlane");
        if (prop != null && typeof(DatumPlane).IsAssignableFrom(prop.PropertyType))
        {
            try
            {
                return prop.GetValue(feat, null) as DatumPlane;
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static bool IsLayerVisible(int layer)
    {
        try
        {
            State s = _workPart.Layers.GetState(layer);
            return s == State.Visible || s == State.Selectable || s == State.WorkLayer;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsTargetLayerEmpty(int layer)
    {
        NXObject[] onLayer = _workPart.Layers.GetAllObjectsOnLayer(layer);
        if (onLayer == null || onLayer.Length == 0)
            return true;

        foreach (NXObject obj in onLayer)
        {
            if (obj == null)
                continue;

            if (obj is DisplayableObject disp)
            {
                if (string.Equals(disp.Name, MirrorPlaneName, StringComparison.Ordinal))
                    continue;
            }

            return false;
        }

        return true;
    }

    // -------------------------------------------------------------------------
    // Import (002)
    // -------------------------------------------------------------------------
    private static void ImportParasolid(string inputFile)
    {
        ParasolidImporter importer = _session.DexManager.CreateParasolidImporter();
        try
        {
            importer.ObjectTypes.Curves = true;
            importer.ObjectTypes.Surfaces = true; // UI "Sheets"
            importer.ObjectTypes.Solids = true;
            importer.FlattenAssembly = true;
            importer.UseActiveLayer = true;
            importer.SetMode(BaseImporter.Mode.NativeFileSystem);
            importer.InputFile = inputFile;

            Session.UndoMarkId mark = _session.SetUndoMark(
                Session.MarkVisibility.Invisible,
                "Import Parasolid");
            importer.Commit();
            _session.DeleteUndoMark(mark, null);
        }
        finally
        {
            importer.Destroy();
        }
    }

    // -------------------------------------------------------------------------
    // Feature group (UF - 003 / 007 not recorded in UI)
    // -------------------------------------------------------------------------
    private static FeatureGroup CreateFeatureGroup(string name, Feature[] features)
    {
        if (features == null || features.Length == 0)
            throw new ArgumentException("No features to group.");

        Tag[] tags = features.Select(f => f.Tag).ToArray();
        Tag groupTag;
        _ufSession.Modl.CreateSetOfFeature(name, tags, tags.Length, 1, out groupTag);

        FeatureGroup fg = _session.GetObjectManager().GetTaggedObject(groupTag) as FeatureGroup;
        if (fg == null)
            throw new Exception("Failed to create feature group '" + name + "'.");
        return fg;
    }

    private static Feature[] GetBrepFeaturesFromBodies(IEnumerable<Body> bodies)
    {
        List<Feature> list = new List<Feature>();
        foreach (Body body in bodies)
        {
            Feature[] feats = body.GetFeatures();
            if (feats == null || feats.Length == 0)
                continue;

            foreach (Feature f in feats)
            {
                if (f != null && !list.Contains(f))
                    list.Add(f);
            }
        }

        if (list.Count == 0)
            throw new Exception("Could not resolve B-rep features for imported bodies.");
        return list.ToArray();
    }

    // -------------------------------------------------------------------------
    // Mirror (004)
    // -------------------------------------------------------------------------
    private static List<Body> MirrorBodies(List<Body> sourceBodies, DatumPlane mirrorDatum)
    {
        HashSet<Tag> before = SnapshotBodyTags();

        Feature nullFeature = null;
        GeomcopyBuilder builder = _workPart.Features.CreateGeomcopyBuilder(nullFeature);
        try
        {
            builder.Type = GeomcopyBuilder.TransformTypes.Mirror;
            builder.Associative = true;
            builder.CopyThreads = true;
            // Preview off: do not call builder.PreviewBuilder.Preview()

            foreach (Body body in sourceBodies)
                builder.GeometryToInstance.Add(body);

            Point3d origin = new Point3d(0.0, 0.0, 0.0);
            Vector3d normal = new Vector3d(0.0, 0.0, 1.0);
            Plane mirrorPlane = _workPart.Planes.CreatePlane(
                origin,
                normal,
                SmartObject.UpdateOption.WithinModeling);
            builder.MirrorPlane = mirrorPlane;

            mirrorPlane.SetMethod(PlaneTypes.MethodType.Distance);
            NXObject[] geom = new NXObject[] { mirrorDatum };
            mirrorPlane.SetGeometry(geom);
            mirrorPlane.SetFlip(false);
            mirrorPlane.SetReverseSide(false);
            mirrorPlane.SetAlternate(PlaneTypes.AlternateType.One);
            mirrorPlane.Evaluate();

            Session.UndoMarkId mark = _session.SetUndoMark(
                Session.MarkVisibility.Invisible,
                "Mirror Geometry");
            builder.CommitFeature();
            _session.DeleteUndoMark(mark, null);
        }
        finally
        {
            builder.Destroy();
        }

        return GetNewSolidAndSheetBodies(before);
    }

    // -------------------------------------------------------------------------
    // Optimize Face (005)
    // -------------------------------------------------------------------------
    private static void OptimizeFaces(List<Body> bodies)
    {
        List<Face> faceList = new List<Face>();
        foreach (Body body in bodies)
        {
            if (body == null)
                continue;
            Face[] faces = body.GetFaces();
            if (faces != null)
                faceList.AddRange(faces);
        }

        if (faceList.Count == 0)
            throw new Exception("No faces found for Optimize Face.");

        OptimizeFaceBuilder builder = _workPart.Features.CreateOptimizeFaceBuilder();
        try
        {
            builder.DistanceTolerance = OptimizeToleranceMm;
            builder.CleanBody = true;
            builder.Report = false;
            SetOptionalOptimizeFlag(builder, "EmphasizeFacesAndEdges", false);
            SetOptionalOptimizeFlag(builder, "EmphasizeFaces", false);
            SetOptionalOptimizeFlag(builder, "CheckFaces", false);

            SelectionIntentRuleOptions ruleOptions =
                _workPart.ScRuleFactory.CreateRuleOptions();
            ruleOptions.SetSelectedFromInactive(false);

            FaceDumbRule faceRule = _workPart.ScRuleFactory.CreateRuleFaceDumb(
                faceList.ToArray(),
                ruleOptions);
            ruleOptions.Dispose();

            SelectionIntentRule[] rules = new SelectionIntentRule[] { faceRule };
            builder.FacesToOptimize.ReplaceRules(rules, false);

            Session.UndoMarkId mark = _session.SetUndoMark(
                Session.MarkVisibility.Invisible,
                "Optimize Face");
            builder.Commit();
            _session.DeleteUndoMark(mark, null);
        }
        finally
        {
            builder.Destroy();
        }
    }

    private static void SetOptionalOptimizeFlag(OptimizeFaceBuilder builder, string propertyName, bool value)
    {
        PropertyInfo prop = typeof(OptimizeFaceBuilder).GetProperty(propertyName);
        if (prop != null && prop.CanWrite && prop.PropertyType == typeof(bool))
            prop.SetValue(builder, value, null);
    }

    // -------------------------------------------------------------------------
    // Delete import group (006)
    // -------------------------------------------------------------------------
    private static void DeleteFeatureGroupAndMembers(FeatureGroup group)
    {
        group.AllowDeleteMembers = true;

        List<TaggedObject> toDelete = new List<TaggedObject>();
        Feature[] members;
        group.GetMembers(out members);
        if (members != null)
        {
            foreach (Feature member in members)
            {
                if (member != null)
                    toDelete.Add(member);
            }
        }

        toDelete.Add(group);

        _session.UpdateManager.ClearErrorList();
        Session.UndoMarkId mark = _session.SetUndoMark(
            Session.MarkVisibility.Visible,
            "Delete IMPORT_A");
        _session.UpdateManager.AddObjectsToDeleteList(toDelete.ToArray());
        _session.UpdateManager.DoUpdate(mark);
    }

    // -------------------------------------------------------------------------
    // Layer move / hide (008)
    // -------------------------------------------------------------------------
    private static void MoveBodiesToLayer(List<Body> bodies, int layer)
    {
        DisplayModification mod = _session.DisplayManager.NewDisplayModification();
        try
        {
            mod.ApplyToAllFaces = true;
            mod.ApplyToOwningParts = false;
            mod.NewLayer = layer;

            DisplayableObject[] objects = bodies
                .Cast<DisplayableObject>()
                .ToArray();
            mod.Apply(objects);
        }
        finally
        {
            mod.Dispose();
        }
    }

    private static void HideLayer(int layer)
    {
        StateInfo[] info = new StateInfo[1];
        info[0].Layer = layer;
        info[0].State = State.Hidden;
        _workPart.Layers.ChangeStates(info, true);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------
    private static HashSet<Tag> SnapshotBodyTags()
    {
        HashSet<Tag> tags = new HashSet<Tag>();
        foreach (Body b in _workPart.Bodies)
            tags.Add(b.Tag);
        return tags;
    }

    private static List<Body> GetNewSolidAndSheetBodies(HashSet<Tag> beforeTags)
    {
        List<Body> list = new List<Body>();
        foreach (Body b in _workPart.Bodies)
        {
            if (beforeTags.Contains(b.Tag))
                continue;
            if (b.IsSolidBody || b.IsSheetBody)
                list.Add(b);
        }
        return list;
    }

    private static IEnumerable<int> GetFileIndices()
    {
        for (int i = MinFileIndex; i <= MaxFileIndex; i++)
            yield return i;
    }

    private static void Fail(string message)
    {
        WriteLog("FATAL: " + message);
        FlushLog();
        _session.ListingWindow.Open();
        _session.ListingWindow.WriteLine("BATCH IMPORT ERROR: " + message);
        _session.ListingWindow.WriteLine("Log: " + _logPath);
        try
        {
            _ufSession.Ui.DisplayMessage(message, 1);
        }
        catch
        {
            // UI may be unavailable in some run modes
        }
    }

    private static void LogHeader()
    {
        _log.Clear();
        _log.AppendLine("=== NX Batch Parasolid Mirror Import ===");
        _log.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        _log.AppendLine("Source: " + SourceFolder);
        _log.AppendLine("Files: " + MinFileIndex + ".x_t .. " + MaxFileIndex + ".x_t");
        _log.AppendLine();
    }

    private static void WriteLog(string line)
    {
        _log.AppendLine(line);
    }

    private static void LogSkip(int index, string reason)
    {
        WriteLog("SKIP [" + index + "]: " + reason);
    }

    private static void LogError(int index, string reason)
    {
        WriteLog("ERROR [" + index + "]: " + reason);
    }
    private static void LogNonProcessedSourceFiles()
    {
        if (!Directory.Exists(SourceFolder))
        {
            WriteLog("NON-PROCESSED: Source folder missing: " + SourceFolder);
            return;
        }

        WriteLog("--- Non-processed / out-of-range files ---");
        string[] files = Directory.GetFiles(SourceFolder, "*.x_t", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        foreach (string path in files)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            int n;
            if (!int.TryParse(name, out n))
            {
                WriteLog("NON-PROCESSED: " + Path.GetFileName(path) + " (not numeric name)");
                continue;
            }

            if (n < MinFileIndex || n > MaxFileIndex)
                WriteLog("NON-PROCESSED: " + Path.GetFileName(path) + " (index " + n + " outside " + MinFileIndex + ".." + MaxFileIndex + ")");
        }
    }

    private static void FlushLog()
    {
        try
        {
            File.WriteAllText(_logPath, _log.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _session.ListingWindow.WriteLine("Could not write log: " + ex.Message);
        }
    }

    public static int GetUnloadOption(string dummy)
    {
        return (int)Session.LibraryUnloadOption.Immediately;
    }
}
