using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace GeoViewer.Core
{
    /// <summary>How many roll (Z) slices the practice grid keeps track of.</summary>
    public enum PracticeMode
    {
        /// <summary>A: every axis 0..90 in 15° steps → 343 combinations per model.</summary>
        Dense = 0,

        /// <summary>B: roll limited to 0 / 45 / 90 → 147 combinations per model.</summary>
        Medium = 1,

        /// <summary>C: roll fixed at 0 → 49 combinations per model.</summary>
        Simple = 2
    }

    /// <summary>
    /// Per model record of the camera angles that have been drawn. Every model keeps
    /// its own set, the record survives restarts and is stored in %APPDATA%.
    /// </summary>
    public sealed class PracticeBook
    {
        public const int Step = 15;

        /// <summary>All preset values of one axis, in degrees.</summary>
        public static readonly int[] Angles = { 0, 15, 30, 45, 60, 75, 90 };

        private static readonly int[] MediumRoll = { 0, 45, 90 };
        private static readonly int[] SimpleRoll = { 0 };

        private readonly Dictionary<int, HashSet<int>> _done = new Dictionary<int, HashSet<int>>();

        /// <summary>Revision of the record currently in memory / on disk, used to merge two windows.</summary>
        private long _revision;

        /// <summary>Why the last <see cref="Save"/> failed, or null when it succeeded.</summary>
        public string? LastSaveError { get; private set; }

        /// <summary>What went wrong while reading the record (restored from backup, corrupt file...).</summary>
        public string? LastLoadNote { get; private set; }

        public PracticeMode Mode { get; private set; } = PracticeMode.Simple;

        public PracticeBook()
        {
            Load();
        }

        public static string FilePath
        {
            get
            {
                // GEO_PRACTICE_DIR lets the record live next to the exe (portable use / testing)
                string? custom = Environment.GetEnvironmentVariable("GEO_PRACTICE_DIR");
                string dir = string.IsNullOrWhiteSpace(custom)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                   "GeoRef", "GeometryViewer")
                    : custom;
                return Path.Combine(dir, "practice.json");
            }
        }

        /// <summary>Roll values that are part of the current mode.</summary>
        public int[] RollLevels
        {
            get
            {
                switch (Mode)
                {
                    case PracticeMode.Dense: return Angles;
                    case PracticeMode.Medium: return MediumRoll;
                    default: return SimpleRoll;
                }
            }
        }

        /// <summary>Number of combinations tracked for one model in the current mode.</summary>
        public int Total => Angles.Length * Angles.Length * RollLevels.Length;

        public static string ModeName(PracticeMode mode)
        {
            switch (mode)
            {
                case PracticeMode.Dense: return "A · 343";
                case PracticeMode.Medium: return "B · 147";
                default: return "C · 49";
            }
        }

        public void SetMode(PracticeMode mode)
        {
            Mode = mode;
            Save();
        }

        public bool AllowsRoll(int roll) => Array.IndexOf(RollLevels, roll) >= 0;

        private static int Pack(int x, int y, int z) =>
            ((x / Step) * Angles.Length + (y / Step)) * Angles.Length + (z / Step);

        private HashSet<int> SetOf(int solid)
        {
            if (!_done.TryGetValue(solid, out HashSet<int>? set))
            {
                set = new HashSet<int>();
                _done[solid] = set;
            }
            return set;
        }

        public bool IsDone(int solid, int x, int y, int z) => SetOf(solid).Contains(Pack(x, y, z));

        /// <summary>Flips the tick of one angle and returns the new state.</summary>
        public bool Toggle(int solid, int x, int y, int z)
        {
            HashSet<int> set = SetOf(solid);
            int key = Pack(x, y, z);
            bool now;
            if (set.Contains(key))
            {
                set.Remove(key);
                now = false;
            }
            else
            {
                set.Add(key);
                now = true;
            }
            Save();
            return now;
        }

        /// <summary>Number of ticked angles that belong to the current mode.</summary>
        public int DoneCount(int solid)
        {
            int count = 0;
            foreach (int key in SetOf(solid))
            {
                int zIndex = key % Angles.Length;
                if (Mode == PracticeMode.Dense
                    || (Mode == PracticeMode.Medium && zIndex % 3 == 0)
                    || (Mode == PracticeMode.Simple && zIndex == 0))
                {
                    count++;
                }
            }
            return count;
        }

        public int Remaining(int solid) => Math.Max(0, Total - DoneCount(solid));

        public void ResetSolid(int solid)
        {
            SetOf(solid).Clear();
            Save();
        }

        /// <summary>Picks a random combination that has not been ticked yet.</summary>
        public bool TryPick(int solid, Random rng, out int x, out int y, out int z)
        {
            x = y = z = 0;
            if (Remaining(solid) <= 0) return false;
            for (int guard = 0; guard < 500; guard++)
            {
                int xi = rng.Next(Angles.Length);
                int yi = rng.Next(Angles.Length);
                int zi = rng.Next(RollLevels.Length);
                int px = Angles[xi], py = Angles[yi], pz = RollLevels[zi];
                if (IsDone(solid, px, py, pz)) continue;
                x = px;
                y = py;
                z = pz;
                return true;
            }
            // fall back to a linear scan so the pick always succeeds
            foreach (int px in Angles)
            {
                foreach (int py in Angles)
                {
                    foreach (int pz in RollLevels)
                    {
                        if (IsDone(solid, px, py, pz)) continue;
                        x = px;
                        y = py;
                        z = pz;
                        return true;
                    }
                }
            }
            return false;
        }

        // ------------------------------------------------------------------ storage

        private sealed class Payload
        {
            public int Version { get; set; } = 2;
            public int Mode { get; set; }

            /// <summary>Bumped on every write, so a stale window can tell it is behind.</summary>
            public long Revision { get; set; }

            public string SavedUtc { get; set; } = string.Empty;

            public Dictionary<string, List<int>> Solids { get; set; } = new Dictionary<string, List<int>>();
        }

        /// <summary>
        /// Reads the record. A missing or broken file must never stop the viewer from starting,
        /// and never silently wipe what is still in memory (that is how ticks disappeared before).
        /// </summary>
        public void Load()
        {
            _done.Clear();
            Payload? data = ReadPayload(out string? note);
            LastLoadNote = note;
            if (data == null) return;
            if (data.Mode >= 0 && data.Mode <= 2) Mode = (PracticeMode)data.Mode;
            Merge(data);
            if (data.Revision > _revision) _revision = data.Revision;

            // The main file could not be read and the ticks came back from the backup:
            // put a healthy file in place right away, but keep the .bak untouched because
            // that is the good copy. Nothing is written when the record was read cleanly.
            if (note != null) SaveCore(mergeFromDisk: false, keepBackup: false);
        }

        /// <summary>
        /// Writes the record atomically and never rolls back what another window saved:
        /// whatever already sits on disk is merged in before the new snapshot goes out.
        /// </summary>
        public bool Save() => SaveCore(mergeFromDisk: true, keepBackup: true);

        private bool SaveCore(bool mergeFromDisk, bool keepBackup)
        {
            LastSaveError = null;
            string path = FilePath;
            string temp = path + ".tmp";

            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                if (mergeFromDisk)
                {
                    Payload? onDisk = ReadPayload(out _);
                    if (onDisk != null)
                    {
                        Merge(onDisk);                    // union only: this can add ticks, never drop them
                        if (onDisk.Revision > _revision) _revision = onDisk.Revision;
                    }
                }

                var data = new Payload
                {
                    Version = 2,
                    Mode = (int)Mode,
                    Revision = _revision + 1,
                    SavedUtc = DateTime.UtcNow.ToString("o")
                };
                foreach (KeyValuePair<int, HashSet<int>> pair in _done)
                {
                    if (pair.Value.Count == 0) continue;
                    data.Solids[pair.Key.ToString()] = new List<int>(pair.Value);
                }

                WriteVerified(path, temp, JsonSerializer.SerializeToUtf8Bytes(data), keepBackup);
                _revision = data.Revision;
                return true;
            }
            catch (Exception ex)
            {
                LastSaveError = ex.Message;
                TryDelete(temp);
                return false;
            }
        }

        /// <summary>Writes to a temp file, proves it parses, then swaps it in (keeping a .bak).</summary>
        private static void WriteVerified(string path, string temp, byte[] bytes, bool keepBackup)
        {
            Exception? last = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
                                                      4096, FileOptions.WriteThrough))
                    {
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }

                    // whatever actually reached the disk has to parse, otherwise we keep the old file
                    using (JsonDocument.Parse(File.ReadAllBytes(temp)))
                    {
                    }

                    if (!keepBackup)
                    {
                        // recovering from the backup: do not let File.Replace move the broken
                        // main file over the .bak, that copy is the only good one left
                        File.Copy(temp, path, true);
                        TryDelete(temp);
                    }
                    else if (File.Exists(path))
                    {
                        try
                        {
                            File.Replace(temp, path, path + ".bak", true);
                        }
                        catch (Exception)
                        {
                            File.Copy(temp, path, true);
                            TryDelete(temp);
                        }
                    }
                    else
                    {
                        File.Move(temp, path);
                    }
                    return;
                }
                catch (Exception ex)
                {
                    // antivirus or another window may hold the file for a moment
                    last = ex;
                    TryDelete(temp);
                    System.Threading.Thread.Sleep(60);
                }
            }
            throw last ?? new IOException("cannot write " + path);
        }

        private Payload? ReadPayload(out string? note)
        {
            note = null;
            string path = FilePath;
            foreach (string candidate in new[] { path, path + ".bak" })
            {
                if (!File.Exists(candidate)) continue;
                try
                {
                    string text = File.ReadAllText(candidate);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    Payload? data = JsonSerializer.Deserialize<Payload>(text);
                    if (data == null) continue;
                    if (!string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase))
                    {
                        note = "主记录损坏，已从备份恢复";
                        KeepCorrupt(path);
                    }
                    return data;
                }
                catch (Exception ex)
                {
                    note = "记录读取失败：" + ex.Message;
                    if (string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase)) KeepCorrupt(path);
                }
            }
            return null;
        }

        private void Merge(Payload data)
        {
            if (data?.Solids == null) return;
            foreach (KeyValuePair<string, List<int>> pair in data.Solids)
            {
                if (!int.TryParse(pair.Key, out int solid) || pair.Value == null) continue;
                HashSet<int> set = SetOf(solid);
                foreach (int key in pair.Value)
                {
                    if (key >= 0 && key <= 342) set.Add(key);
                }
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception)
            {
                // nothing sensible to do
            }
        }

        private static void KeepCorrupt(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                string copy = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                if (!File.Exists(copy)) File.Copy(path, copy, true);
            }
            catch (Exception)
            {
                // nothing sensible to do
            }
        }
    }
}
