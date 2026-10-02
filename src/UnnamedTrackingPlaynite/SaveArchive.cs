using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;

namespace UnnamedTrackingPlaynite;

internal static class SaveArchive
{
    private static bool UnsafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "." || name == ".." || name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Any(character => character < 32 || "<>:\"/\\|?*".Contains(character))) return true;
        var stem = name.Split('.')[0];
        return new[] { "CON", "PRN", "AUX", "NUL" }.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] >= '1' && stem[3] <= '9');
    }

    internal static List<SavePathEntry> ValidatePaths(IEnumerable<SavePathEntry> entries)
    {
        var paths = entries.Select(entry => new SavePathEntry
        {
            Name = entry.Name?.Trim() ?? string.Empty,
            Path = Environment.ExpandEnvironmentVariables(entry.Path?.Trim() ?? string.Empty),
            IsFile = entry.IsFile || File.Exists(Environment.ExpandEnvironmentVariables(entry.Path ?? string.Empty))
        }).ToList();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in paths)
        {
            if (UnsafeName(entry.Name) || !names.Add(entry.Name))
                throw new InvalidOperationException("Save location names must be unique, non-empty file names.");
            if (string.IsNullOrWhiteSpace(entry.Path) || !Path.IsPathRooted(entry.Path) || entry.Path.Contains("%"))
                throw new InvalidOperationException("Save paths must be absolute, with all environment variables resolved.");
            if (Path.DirectorySeparatorChar == '\\' && !(entry.Path.StartsWith("\\\\", StringComparison.Ordinal) ||
                (entry.Path.Length >= 3 && char.IsLetter(entry.Path[0]) && entry.Path[1] == ':' && (entry.Path[2] == '\\' || entry.Path[2] == '/'))))
                throw new InvalidOperationException("Save paths must include their drive or a complete network-share root.");
            entry.Path = Path.GetFullPath(entry.Path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(entry.Path) || string.Equals(entry.Path, Path.GetPathRoot(entry.Path)?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A drive root cannot be used as a save location.");
            RejectReparsePoints(entry.Path);
        }
        for (var i = 0; i < paths.Count; i++)
            for (var j = i + 1; j < paths.Count; j++)
                if (IsWithin(paths[i].Path, paths[j].Path) || IsWithin(paths[j].Path, paths[i].Path))
                    throw new InvalidOperationException("Save locations must not duplicate or overlap each other.");
        return paths;
    }

    private static bool IsWithin(string path, string root) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static void RejectReparsePoints(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Save paths cannot traverse symbolic links or junctions: " + current);
    }

    internal static List<string> CollectFiles(string root)
    {
        RejectReparsePoints(root);
        if (File.Exists(root)) return new List<string> { root };
        var result = new List<string>();
        if (!Directory.Exists(root)) return result;
        foreach (var file in Directory.GetFiles(root)) { RejectReparsePoints(file); result.Add(file); }
        foreach (var child in Directory.GetDirectories(root)) result.AddRange(CollectFiles(child));
        return result;
    }

    [DataContract]
    private sealed class RestoreFile
    {
        [DataMember] public string Destination { get; set; } = string.Empty;
        [DataMember] public string Backup { get; set; } = string.Empty;
        [DataMember] public bool Existed { get; set; }
        public string Staged { get; set; } = string.Empty;
    }

    // Fully validate and decompress before changing saves. A durable backup and
    // recovery manifest precede each commit; failures/cancellation roll it back.
    internal static string Restore(string zip, SavePathEntry location, string backupDirectory, CancellationToken token)
    {
        location = ValidatePaths(new[] { location }).Single();
        var stage = Path.Combine(Path.GetTempPath(), "unnamed-save-stage-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(backupDirectory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var files = new List<RestoreFile>();
        try
        {
            var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long expanded = 0;
            using (var archive = ZipFile.OpenRead(zip))
            {
                if (archive.Entries.Count > 100000) throw new InvalidDataException("Save archive contains too many entries.");
                foreach (var entry in archive.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    var parts = entry.FullName.Replace('\\', '/').Split('/');
                    if (parts.Length < 2 || !string.Equals(parts[0], location.Name, StringComparison.OrdinalIgnoreCase) ||
                        parts.Take(parts.Length - (string.IsNullOrEmpty(entry.Name) ? 1 : 0)).Any(UnsafeName))
                        throw new InvalidDataException("Save archive contains an unsafe or unknown save location.");
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    if (location.IsFile && (parts.Length != 2 || !string.Equals(parts[1], Path.GetFileName(location.Path), StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("Save archive does not match the configured save file.");
                    var destination = location.IsFile ? location.Path : Path.GetFullPath(Path.Combine(location.Path, Path.Combine(parts.Skip(1).ToArray())));
                    if (!location.IsFile && (!IsWithin(destination, location.Path) || string.Equals(destination, location.Path, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("Unsafe save archive path.");
                    RejectReparsePoints(destination);
                    if (!destinations.Add(destination) || Directory.Exists(destination))
                        throw new InvalidDataException("Save archive contains a duplicate or conflicting destination.");
                    expanded = checked(expanded + entry.Length);
                    if (expanded > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Save archive exceeds the 2 GiB expanded safety limit.");
                    var staged = Path.Combine(stage, files.Count.ToString());
                    using (var input = entry.Open())
                    using (var output = File.Create(staged))
                    {
                        var buffer = new byte[81920];
                        int read;
                        long written = 0;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            token.ThrowIfCancellationRequested();
                            written = checked(written + read);
                            if (written > entry.Length) throw new InvalidDataException("Save archive expanded beyond its declared size.");
                            output.Write(buffer, 0, read);
                        }
                        if (written != entry.Length) throw new InvalidDataException("Save archive is truncated.");
                    }
                    files.Add(new RestoreFile { Destination = destination, Staged = staged, Existed = File.Exists(destination), Backup = Path.Combine(backup, files.Count.ToString()) });
                }
            }
            if (files.Count == 0) throw new InvalidDataException("Save archive contains no save files.");
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(backup);
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                if (file.Existed) File.Copy(file.Destination, file.Backup, false);
            }
            using (var manifest = File.Create(Path.Combine(backup, "recovery.json")))
                new DataContractJsonSerializer(typeof(List<RestoreFile>)).WriteObject(manifest, files);
            var changed = new List<RestoreFile>();
            try
            {
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    RejectReparsePoints(file.Destination);
                    Directory.CreateDirectory(Path.GetDirectoryName(file.Destination)!);
                    changed.Add(file);
                    File.Copy(file.Staged, file.Destination, true);
                }
            }
            catch (Exception original)
            {
                try
                {
                    foreach (var file in changed.AsEnumerable().Reverse())
                    {
                        if (file.Existed) File.Copy(file.Backup, file.Destination, true);
                        else if (File.Exists(file.Destination)) File.Delete(file.Destination);
                    }
                }
                catch (Exception rollback)
                {
                    throw new IOException("Save restore and rollback failed. Recover the originals from " + backup, new AggregateException(original, rollback));
                }
                throw;
            }
            return backup;
        }
        finally { try { Directory.Delete(stage, true); } catch { } }
    }
}
