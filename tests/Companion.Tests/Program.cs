using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using UnnamedTrackingPlaynite;

internal static class Program
{
    private static int assertions;
    private static readonly TestLogger Log = new TestLogger();

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { assertions++; return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { assertions++; return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static async Task Main()
    {
        StatusAndIdentity();
        ConfigurationAndPaths();
        SaveRestore();
        await LibraryHttp();
        await SaveHttp();
        Console.WriteLine("Passed " + assertions + " assertions against production synchronization/save helpers.");
    }

    private static void StatusAndIdentity()
    {
        var cases = new Dictionary<string, string>
        {
            ["Wishlist"] = "WISHLIST", ["wish list"] = "WISHLIST", ["Dropped"] = "DROPPED",
            ["On Hold"] = "ON_HOLD", ["Playing"] = "PLAYING", ["In progress"] = "PLAYING",
            ["Mastered"] = "MASTERED", ["Beaten"] = "BEATEN", ["Completed"] = "BEATEN",
            ["complete"] = "BEATEN", ["Played"] = "PLAYED", ["Backlog"] = "BACKLOG",
            ["Plan to play"] = "BACKLOG", ["Abandoned"] = "BACKLOG", ["100%"] = "BACKLOG",
            ["未知"] = "BACKLOG"
        };
        foreach (var pair in cases)
            Check(UnnamedTrackingSyncClient.NormalizeStatus("  " + pair.Key.ToUpperInvariant() + "  ", 100) == pair.Value, "Status: " + pair.Key);
        Check(UnnamedTrackingSyncClient.NormalizeStatus(null, 0) == "BACKLOG", "Empty/unplayed fallback");
        Check(UnnamedTrackingSyncClient.NormalizeStatus(" ", 1) == "PLAYED", "Empty/played fallback");
        Check(UnnamedTrackingSyncClient.MatchesIgnoreTag(new[] { "  TRACKINGAPP_IGNORE  " }, "trackingapp_ignore"), "Ignore matching trims and ignores case");
        Check(!UnnamedTrackingSyncClient.MatchesIgnoreTag(new[] { "trackingapp_ignore" }, ""), "Empty ignore filter stays disabled");
        Check(!UnnamedTrackingSyncClient.MatchesIgnoreTag(new[] { "other" }, "trackingapp_ignore"), "Other tags are retained");
        Throws<InvalidOperationException>(() => ApiConnection.ValidateKey("utpm_management"));
        Throws<InvalidOperationException>(() => ApiConnection.ValidateKey("utk_bad\nheader"));
        var guid = Guid.NewGuid();
        var payload = new UnnamedTrackingSyncGamePayload { PlayniteGuid = guid, FolderLocation = "new-folder" };
        var linked = new UnnamedTrackingSyncExistingGame { Id = Guid.NewGuid(), PlayniteGuid = guid, FolderLocation = "old-folder" };
        var conflicting = new UnnamedTrackingSyncExistingGame { Id = Guid.NewGuid(), PlayniteGuid = Guid.NewGuid(), FolderLocation = "new-folder" };
        Check(UnnamedTrackingSyncClient.MatchGame(new[] { conflicting, linked }, payload) == linked, "GUID wins over folder after rename");
        Throws<InvalidDataException>(() => UnnamedTrackingSyncClient.MatchGame(new[] { linked, linked }, payload));
        Throws<InvalidDataException>(() => UnnamedTrackingSyncClient.MatchGame(new[] { conflicting }, payload));
        var legacy = new UnnamedTrackingSyncExistingGame { Id = Guid.NewGuid(), FolderLocation = "playnite-" + guid.ToString("N") };
        Check(UnnamedTrackingSyncClient.MatchGame(new[] { legacy }, payload) == legacy, "Legacy folder migration");
        Check(UnnamedTrackingSyncClient.MatchGame(new UnnamedTrackingSyncExistingGame[0], payload) == null, "New GUID");
        foreach (var url in new[] { "file:///tmp/test", "https://user:password@example.com", "https://example.com?key=utk_secret", "https://example.com/#token", "junk" })
            Throws<InvalidOperationException>(() => ApiConnection.ValidateBaseUrl(url));
        Check(ApiConnection.ValidateBaseUrl(" https://example.com/tracking/ ") == "https://example.com/tracking", "Reverse proxy base path");
        Throws<InvalidOperationException>(() => ApiConnection.DownloadUrl("https://example.com", "//evil.example/file", guid, guid, guid));
        var expected = $"/api/game/{guid}/archives/{guid}/versions/{guid}/download";
        Check(ApiConnection.DownloadUrl("https://example.com/base", expected, guid, guid, guid) == "https://example.com/base" + expected, "Download identity and same origin");
    }

    private static void ConfigurationAndPaths()
    {
        using var temp = new TemporaryDirectory();
        var id = Guid.NewGuid();
        // Old serializers can omit every collection, including games entirely.
        File.WriteAllText(Path.Combine(temp.Path, "save-sync.json"), "{}");
        var store = new SaveSyncStore(temp.Path, Log);
        var configuration = store.Snapshot(id);
        Check(configuration.SavePaths.Count == 0 && configuration.RemoteArchiveIds.Count == 0 && configuration.LocationFingerprints.Count == 0, "Old collections normalized");
        var folder = Path.Combine(temp.Path, "saves");
        Directory.CreateDirectory(folder);
        var manager = new SaveSyncManager(temp.Path, Log);
        manager.SaveConfiguration(id, new[] { new SavePathEntry { Name = "Main", Path = folder } }, true, true);
        var reloaded = new SaveSyncManager(temp.Path, Log).Configuration(id);
        Check(reloaded.SavePaths.Single().Path == folder && reloaded.UploadOnGameStop && reloaded.DownloadOnGameStart, "Configuration survives reload");
        reloaded.RemoteArchiveIds[folder.ToUpperInvariant()] = Guid.NewGuid();
        store.Commit(id, reloaded);
        Check(new SaveSyncStore(temp.Path, Log).Snapshot(id).RemoteArchiveIds.ContainsKey(folder.ToLowerInvariant()), "Path comparer survives JSON roundtrip");
        Check(File.Exists(Path.Combine(temp.Path, "save-sync.json.bak")), "Atomic replacement retains previous configuration");
        File.WriteAllText(Path.Combine(temp.Path, "save-sync.json"), "broken");
        var recovered = new SaveSyncStore(temp.Path, Log);
        Check(recovered.Snapshot(id).SavePaths.Count == 0 && Directory.GetFiles(temp.Path, "*.corrupt-*").Length == 1, "Corrupt configuration preserved");
        Throws<InvalidOperationException>(() => SaveArchive.ValidatePaths(new[] { new SavePathEntry { Name = "Main", Path = "relative/path" } }));
        Throws<InvalidOperationException>(() => SaveArchive.ValidatePaths(new[] { new SavePathEntry { Name = "../Main", Path = folder } }));
        foreach (var name in new[] { "CON", "LPT1.sav", "Main.", "Bad\nname", "a:b" })
            Throws<InvalidOperationException>(() => SaveArchive.ValidatePaths(new[] { new SavePathEntry { Name = name, Path = folder } }));
#if NET462
        foreach (var path in new[] { "C:relative", "\\relative" })
            Throws<InvalidOperationException>(() => SaveArchive.ValidatePaths(new[] { new SavePathEntry { Name = "Main", Path = path } }));
#endif
        Throws<InvalidOperationException>(() => SaveArchive.ValidatePaths(new[] { new SavePathEntry { Name = "Main", Path = System.IO.Path.GetPathRoot(folder)! } }));
        Throws<InvalidOperationException>(() => SaveArchive.ValidatePaths(new[] { new SavePathEntry { Name = "Main", Path = folder }, new SavePathEntry { Name = "main", Path = folder + "2" } }));
        Throws<InvalidOperationException>(() => SaveArchive.ValidatePaths(new[] { new SavePathEntry { Name = "Main", Path = folder }, new SavePathEntry { Name = "Other", Path = Path.Combine(folder, "nested") } }));
        Throws<InvalidOperationException>(() => SaveArchive.ValidatePaths(new[] { new SavePathEntry { Name = "Main", Path = folder }, new SavePathEntry { Name = "Other", Path = folder } }));
        var snapshot = manager.Configuration(id);
        snapshot.SavePaths.Clear();
        Check(manager.Configuration(id).SavePaths.Count == 1, "UI configuration is detached from worker state");
    }

    private static string Zip(TemporaryDirectory temp, params KeyValuePair<string, string>[] entries)
    {
        var path = Path.Combine(temp.Path, Guid.NewGuid().ToString("N") + ".zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        foreach (var pair in entries)
        using (var writer = new StreamWriter(zip.CreateEntry(pair.Key).Open())) writer.Write(pair.Value);
        return path;
    }
    private static KeyValuePair<string, string> Entry(string path, string content) => new KeyValuePair<string, string>(path, content);

    private static void SaveRestore()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "local");
        Directory.CreateDirectory(root);
        var local = Path.Combine(root, "slot.sav");
        File.WriteAllText(local, "original");
        var location = new SavePathEntry { Name = "Main", Path = root };
        var backups = Path.Combine(temp.Path, "backups");
        // Unsafe last entry must not permit earlier valid entries to overwrite saves.
        foreach (var path in new[] { "Main/../../escape.sav", "Unknown/slot.sav", "/Main/slot.sav", "Main/C:/slot.sav", "Main/slot.sav", "Main/sub/../slot.sav", "Main/CON", "Main/a:b", "Main/trailing." })
        {
            var invalid = Zip(temp, Entry("Main/slot.sav", "changed"), Entry(path, "bad"));
            Throws<InvalidDataException>(() => SaveArchive.Restore(invalid, location, backups, CancellationToken.None));
            Check(File.ReadAllText(local) == "original", "Invalid archive leaves original unchanged: " + path);
        }
        var valid = Zip(temp, Entry("Main/slot.sav", "cloud"), Entry("Main/sub/new.sav", "new"));
        var backup = SaveArchive.Restore(valid, location, backups, CancellationToken.None);
        Check(File.ReadAllText(local) == "cloud" && File.ReadAllText(Path.Combine(root, "sub/new.sav")) == "new", "Valid archive restored");
        Check(File.ReadAllText(Path.Combine(backup, "0")) == "original" && File.Exists(Path.Combine(backup, "recovery.json")), "Durable original and recovery manifest");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Throws<OperationCanceledException>(() => SaveArchive.Restore(valid, location, backups, cancelled.Token));
            Check(File.ReadAllText(local) == "cloud", "Cancellation preserves saves");
        }
        var fileLocation = new SavePathEntry { Name = "Single", Path = local, IsFile = true };
        SaveArchive.Restore(Zip(temp, Entry("Single/slot.sav", "single")), fileLocation, backups, CancellationToken.None);
        Check(File.ReadAllText(local) == "single" && !Directory.Exists(local), "Single-file target is a file");
        Throws<InvalidDataException>(() => SaveArchive.Restore(Zip(temp, Entry("Single/other.sav", "bad")), fileLocation, backups, CancellationToken.None));
        // Force a commit failure on the second destination after first was copied.
        var blocking = Path.Combine(root, "blocked");
        File.WriteAllText(blocking, "file, not directory");
        Throws<IOException>(() => SaveArchive.Restore(Zip(temp, Entry("Main/slot.sav", "first"), Entry("Main/blocked/next.sav", "second")), location, backups, CancellationToken.None));
        Check(File.ReadAllText(local) == "single" && File.ReadAllText(blocking) == "file, not directory", "Commit failure rolls earlier writes back");
        Check(!File.Exists(Path.Combine(temp.Path, "escape.sav")), "No traversal write");
        var unavailableBackup = Path.Combine(temp.Path, "not-a-directory");
        File.WriteAllText(unavailableBackup, "occupied");
        Throws<IOException>(() => SaveArchive.Restore(valid, location, unavailableBackup, CancellationToken.None));
        Check(File.ReadAllText(local) == "single", "Backup failure leaves saves unchanged");
        var missingFile = Path.Combine(root, "missing.sav");
        SaveArchive.Restore(Zip(temp, Entry("Missing/missing.sav", "restored")), new SavePathEntry { Name = "Missing", Path = missingFile, IsFile = true }, backups, CancellationToken.None);
        Check(File.ReadAllText(missingFile) == "restored", "Persisted file target restores when absent");
#if NET8_0
        var link = Path.Combine(temp.Path, "symlink");
        Directory.CreateSymbolicLink(link, root);
        Throws<InvalidDataException>(() => SaveArchive.ValidatePaths(new[] { new SavePathEntry { Name = "Link", Path = link } }));
        var nestedLink = Path.Combine(root, "linked-subfolder");
        Directory.CreateSymbolicLink(nestedLink, temp.Path);
        Throws<InvalidDataException>(() => SaveArchive.CollectFiles(root));
        Directory.Delete(nestedLink);
        Directory.Delete(link);
#endif
    }

    private static SyncGameSnapshot Snapshot(Guid id, bool ignored = false) => new SyncGameSnapshot
    {
        Payload = new UnnamedTrackingSyncGamePayload { Title = "Game", PlayniteGuid = id, FolderLocation = "playnite-Game-" + id.ToString("N"), Status = "BACKLOG" }, Ignored = ignored
    };
    private static string Remote(Guid id, Guid guid, string folder = "old-folder") => $"{{\"id\":\"{id}\",\"playnite_guid\":\"{guid}\",\"folder_location\":\"{folder}\"}}";

    private static async Task LibraryHttp()
    {
        var client = new UnnamedTrackingSyncClient(Log, null!);
        var guid = Guid.NewGuid();
        var remote = Guid.NewGuid();
        using (var server = new Server(request => request.HttpMethod == "GET" ? "[]" : $"{{\"id\":\"{remote}\"}}"))
        {
            var result = await client.UploadSnapshotsAsync(server.Url, "utk_test", new[] { Snapshot(guid), Snapshot(guid), Snapshot(Guid.NewGuid(), true) });
            Check(result.SucceededGames == 1 && result.TotalGames == 1, "Duplicates and ignored games filtered");
            Check(server.Requests.Count(request => request.Method == "POST") == 1, "One create request per GUID");
            Check(server.Requests.All(request => request.Authorization == "Bearer utk_test"), "Header authentication");
            Check(server.Requests.Single(request => request.Method == "POST").Body.Contains("BACKLOG"), "Create contains valid finite status");
            Check(!Log.Messages.Any(message => message.Contains("utk_test")), "API key absent from logs");
        }
        using (var server = new Server(request => request.HttpMethod == "GET" ? "[" + Remote(remote, guid) + "]" : "{}"))
        {
            var result = await client.UploadSnapshotsAsync(server.Url, "utk_test", new[] { Snapshot(guid) });
            Check(result.SucceededGames == 1 && server.Requests.Count(request => request.Method == "POST") == 0, "Rename updates by GUID");
            Check(server.Requests.Single(request => request.Method == "PATCH").Body.Contains("old-folder"), "Remote folder remains stable");
        }
        foreach (var body in new[] { "null", "{}", "garbage", "", "[{\"id\":\"00000000-0000-0000-0000-000000000000\"}]" })
        using (var server = new Server(request => body))
        {
            await ThrowsAsync<Exception>(() => client.UploadSnapshotsAsync(server.Url, "utk_test", new[] { Snapshot(guid) }));
            Check(server.Requests.All(request => request.Method == "GET"), "Malformed lookup never creates: " + body);
            await ThrowsAsync<Exception>(() => client.TestConnectionAsync(server.Url, "utk_test"));
        }
        foreach (var status in new[] { 401, 403, 404, 409, 422, 500 })
        using (var server = new Server(request => "utk_test", status))
        {
            var result = await client.UploadSnapshotsAsync(server.Url, "utk_test", new[] { Snapshot(guid) });
            Check(result.FailedGames == 1 && result.Failures[0].StatusCode == status, "HTTP failure attributed: " + status);
            Check(!result.Failures[0].ResponseBody.Contains("utk_test"), "Echoed credential redacted");
            await ThrowsAsync<UnnamedTrackingSyncApiException>(() => client.TestConnectionAsync(server.Url, "utk_test"));
        }
        using (var server = new Server(request => "", 302))
            await ThrowsAsync<Exception>(() => client.TestConnectionAsync(server.Url, "utk_test"));
        using (var cancelled = new CancellationTokenSource())
        using (var server = new Server(request => "[]"))
        {
            cancelled.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => client.UploadSnapshotsAsync(server.Url, "utk_test", new[] { Snapshot(guid) }, cancelled.Token));
            Check(server.Requests.Count == 0, "Pre-cancelled sync never calls server");
        }
        using (var server = new Server(request => "[]", delay: true))
        using (var cancelled = new CancellationTokenSource())
        {
            var task = client.UploadSnapshotsAsync(server.Url, "utk_test", new[] { Snapshot(guid) }, cancelled.Token);
            await server.Arrived.Task;
            cancelled.Cancel();
            Check(await Task.WhenAny(task, Task.Delay(5000)) == task, "In-flight lookup cancellation completes promptly");
            await ThrowsAsync<OperationCanceledException>(() => task);
            Check(server.Requests.All(request => request.Method == "GET"), "Cancellation never starts create");
        }
        using (var server = new Server(request => request.HttpMethod == "GET" ? "[]" : $"{{\"id\":\"{remote}\"}}"))
        using (var cancelled = new CancellationTokenSource())
        {
            await ThrowsAsync<OperationCanceledException>(() => client.UploadSnapshotsAsync(server.Url, "utk_test", new[] { Snapshot(guid), Snapshot(Guid.NewGuid()) }, cancelled.Token,
                (completed, total, name) => { if (completed == 1) cancelled.Cancel(); }));
            Check(server.Requests.Count(request => request.Method == "POST") == 1, "Cancellation between games prevents next create");
        }
        using (var server = new Server(request => "[]", delay: true))
        using (var firstCancellation = new CancellationTokenSource())
        using (var queuedCancellation = new CancellationTokenSource())
        {
            var first = client.UploadSnapshotsAsync(server.Url, "utk_test", new[] { Snapshot(guid) }, firstCancellation.Token);
            await server.Arrived.Task;
            var queued = client.UploadSnapshotsAsync(server.Url, "utk_test", new[] { Snapshot(guid) }, queuedCancellation.Token);
            queuedCancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => queued);
            Check(server.Requests.Count == 1, "Cancelled queued mutation never overlaps lookup");
            firstCancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => first);
        }
        using (var server = new Server(request => "[" + Remote(remote, guid) + "]"))
        {
            var preview = await client.PreviewSnapshotsAsync(server.Url, "utk_test", new[] { Snapshot(guid), Snapshot(Guid.NewGuid()), Snapshot(Guid.NewGuid(), true) });
            Check(preview.WouldUpdate == 1 && preview.WouldCreate == 1 && preview.Ignored == 1, "Preview matches and counts ignores");
            Check(server.Requests.All(request => request.Method == "GET"), "Preview has no writes");
        }
        using (var server = new Server(request => "[]"))
        {
            Check(!await client.UpdateSnapshotAsync(server.Url, "utk_test", Snapshot(guid)), "Game-stop skips unlinked game");
            Check(server.Requests.All(request => request.Method == "GET"), "Game-stop never creates");
            var count = server.Requests.Count;
            Check(!await client.UpdateSnapshotAsync(server.Url, "utk_test", Snapshot(guid, true)) && count == server.Requests.Count, "Ignored game-stop performs no request");
        }
        using (var temp = new TemporaryDirectory())
        using (var server = new Server(request => request.HttpMethod == "GET" ? "[]" : $"{{\"id\":\"{remote}\"}}", artworkStatus: 500))
        {
            var snapshot = Snapshot(guid);
            snapshot.CoverImage = Path.Combine(temp.Path, "cover.png"); File.WriteAllText(snapshot.CoverImage, "fixture");
            var result = await client.UploadSnapshotsAsync(server.Url, "utk_test", new[] { snapshot });
            Check(result.SucceededGames == 1 && result.FailedGames == 0 && result.WarningCount == 1, "Artwork failure remains a warning");
        }
        // A second page must be scanned before deciding to create.
        using (var server = new Server(request => request.Url!.Query.Contains("skip=0") ? "[" + string.Join(",", Enumerable.Range(0, 200).Select(i => Remote(Guid.NewGuid(), Guid.NewGuid(), "folder-" + i))) + "]" : request.HttpMethod == "GET" ? "[" + Remote(remote, guid) + "]" : "{}"))
        {
            var result = await client.UploadSnapshotsAsync(server.Url, "utk_test", new[] { Snapshot(guid) });
            Check(result.SucceededGames == 1 && server.Requests.Count(request => request.Method == "GET") == 2, "Library pagination");
            Check(server.Requests.All(request => request.Method != "POST"), "Second-page GUID prevents duplicate");
        }
        var repeated = "[" + string.Join(",", Enumerable.Range(0, 200).Select(i => Remote(Guid.NewGuid(), Guid.NewGuid(), "folder-" + i))) + "]";
        using (var server = new Server(request => repeated))
            await ThrowsAsync<InvalidDataException>(() => client.UploadSnapshotsAsync(server.Url, "utk_test", new[] { Snapshot(guid) }));
    }

    private static async Task SaveHttp()
    {
        using var temp = new TemporaryDirectory();
        var game = Guid.NewGuid(); var remote = Guid.NewGuid(); var archive = Guid.NewGuid();
        var root = Path.Combine(temp.Path, "saves"); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "slot.sav"), "local");
        var manager = new SaveSyncManager(temp.Path, Log);
        manager.SaveConfiguration(game, new[] { new SavePathEntry { Name = "Main", Path = root } }, true, true);
        using (var server = new Server(request => request.Url!.AbsolutePath.EndsWith("/list", StringComparison.Ordinal)
            ? request.Url.Query.Contains("skip=0") ? "[" + string.Join(",", Enumerable.Range(0, 200).Select(i => Remote(Guid.NewGuid(), Guid.NewGuid()))) + "]" : "[" + Remote(remote, game) + "]"
            : request.HttpMethod == "GET" ? "[]" : $"{{\"id\":\"{archive}\"}}"))
        {
            await manager.UploadAsync(game, "Game", server.Url, "utk_test");
            Check(server.Requests.Count(request => request.Path.StartsWith("/api/game/list", StringComparison.Ordinal)) == 2, "Save GUID lookup pages beyond 200 games");
            Check(manager.Configuration(game).RemoteArchiveIds[root] == archive, "Archive ID persisted");
        }
        using (var server = new Server(request => request.Url!.AbsolutePath.EndsWith("/list", StringComparison.Ordinal) ? "[" + Remote(remote, game) + "]" : $"[{{\"id\":\"{archive}\",\"name\":\"Main\",\"updated_at\":1.25,\"versions\":[]}}]"))
        {
            await manager.UploadAsync(game, "Game", server.Url, "utk_test", true);
            Check(server.Requests.All(request => request.Method == "GET"), "Unchanged automatic upload skipped");
            var saveFile = Path.Combine(root, "slot.sav"); var stamp = File.GetLastWriteTimeUtc(saveFile);
            File.WriteAllText(saveFile, "other"); File.SetLastWriteTimeUtc(saveFile, stamp);
            await manager.UploadAsync(game, "Game", server.Url, "utk_test", true);
            Check(server.Requests.Any(request => request.Method == "POST" && request.Path.EndsWith("/versions", StringComparison.Ordinal)), "Same-size timestamp-preserved edit uploads a version");
        }
        var version = Guid.NewGuid();
        var downloadPath = $"/api/game/{remote}/archives/{archive}/versions/{version}/download";
        var archives = $"[{{\"id\":\"{archive}\",\"name\":\"Main\",\"versions\":[{{\"id\":\"{version}\",\"uploaded_at\":10.75,\"url\":\"{downloadPath}\"}}]}}]";
        var zip = Zip(temp, Entry("Main/slot.sav", "cloud"));
        using (var server = new Server(request => request.Url!.AbsolutePath.EndsWith("/list", StringComparison.Ordinal) ? "[" + Remote(remote, game) + "]" : archives,
            binaryResponse: request => File.ReadAllBytes(zip)))
        {
            await manager.DownloadAsync(game, "Game", server.Url, "utk_test");
            Check(File.ReadAllText(Path.Combine(root, "slot.sav")) == "cloud", "Remote version download restores save");
            Check(Directory.GetFiles(Path.Combine(temp.Path, "save-backups"), "recovery.json", SearchOption.AllDirectories).Length == 1, "Remote restore retains recovery manifest");
            Check(server.Requests.Any(request => request.Path == downloadPath && request.Authorization == "Bearer utk_test"), "Download uses selected identity and header");
        }
        foreach (var malformed in new[] { "null", "{}", "[{\"id\":\"" + archive + "\",\"name\":\"Main\",\"versions\":null}]", archives.Replace(downloadPath, "//evil.invalid/archive") })
        using (var server = new Server(request => request.Url!.AbsolutePath.EndsWith("/list", StringComparison.Ordinal) ? "[" + Remote(remote, game) + "]" : malformed))
        {
            await ThrowsAsync<Exception>(() => manager.DownloadAsync(game, "Game", server.Url, "utk_test"));
            Check(File.ReadAllText(Path.Combine(root, "slot.sav")) == "cloud", "Invalid archive response preserves saves");
            Check(!server.Requests.Any(request => request.Path.Contains("/download")), "Invalid download response never fetches external URL");
        }
        File.WriteAllText(Path.Combine(root, "slot.sav"), "other");
        // Removed remote archive must not cause unchanged local content to be skipped.
        using (var server = new Server(request => request.Url!.AbsolutePath.EndsWith("/list", StringComparison.Ordinal) ? "[" + Remote(remote, game) + "]" : request.HttpMethod == "GET" ? "[]" : $"{{\"id\":\"{Guid.NewGuid()}\"}}"))
        {
            await manager.UploadAsync(game, "Game", server.Url, "utk_test", true);
            Check(server.Requests.Any(request => request.Method == "POST" && request.Path.EndsWith("/save", StringComparison.Ordinal)), "Missing remote archive is recreated");
        }
        var secondRoot = Path.Combine(temp.Path, "second"); Directory.CreateDirectory(secondRoot);
        File.WriteAllText(Path.Combine(secondRoot, "profile.sav"), "profile");
        manager.SaveConfiguration(game, new[] { new SavePathEntry { Name = "Main", Path = root }, new SavePathEntry { Name = "Profiles", Path = secondRoot } }, true, true);
        using (var server = new Server(request => request.Url!.AbsolutePath.EndsWith("/list", StringComparison.Ordinal) ? "[" + Remote(remote, game) + "]" : request.HttpMethod == "GET" ? "[]" : $"{{\"id\":\"{Guid.NewGuid()}\"}}"))
        {
            await manager.UploadAsync(game, "Game", server.Url, "utk_test");
            Check(server.Requests.Count(request => request.Method == "POST") == 2, "Multiple save locations upload separately");
            Check(new SaveSyncStore(temp.Path, Log).Snapshot(game).RemoteArchiveIds.Count == 2, "Multiple archive associations persist");
        }
        foreach (var status in new[] { 401, 500 })
        using (var server = new Server(request => "error", status))
        {
            await ThrowsAsync<WebException>(() => manager.DownloadAsync(game, "Game", server.Url, "utk_test"));
            Check(File.ReadAllText(Path.Combine(root, "slot.sav")) == "other", "HTTP failure preserves saves");
        }
        using (var server = new Server(request => "[]", delay: true))
        {
            var task = manager.UploadAsync(game, "Game", server.Url, "utk_test");
            await server.Arrived.Task;
            Throws<InvalidOperationException>(() => manager.SaveConfiguration(game, new SavePathEntry[0], false, false));
            manager.Cancel();
            Check(await Task.WhenAny(task, Task.Delay(5000)) == task, "Shutdown cancels in-flight save request");
            await ThrowsAsync<OperationCanceledException>(() => task);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "playnite-tests-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() { Directory.CreateDirectory(Path); }
        public void Dispose() { Directory.Delete(Path, true); }
    }
    private sealed class RequestRecord
    {
        public string Method = ""; public string Path = ""; public string Body = ""; public string Authorization = "";
    }
    private sealed class Server : IDisposable
    {
        private readonly HttpListener listener = new HttpListener();
        private readonly Task worker;
        private readonly TaskCompletionSource<bool> release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Arrived { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<RequestRecord> Requests { get; } = new List<RequestRecord>();
        public string Url { get; }
        public Server(Func<HttpListenerRequest, string> response, int status = 200, bool delay = false, int? artworkStatus = null, Func<HttpListenerRequest, byte[]>? binaryResponse = null)
        {
            var port = new TcpListener(IPAddress.Loopback, 0); port.Start();
            var number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
            Url = "http://localhost:" + number;
            listener.Prefixes.Add(Url + "/"); listener.Start();
            worker = Task.Run(async () =>
            {
                try
                {
                    while (listener.IsListening)
                    {
                        var context = await listener.GetContextAsync();
                        string body; using (var reader = new StreamReader(context.Request.InputStream)) body = await reader.ReadToEndAsync();
                        Requests.Add(new RequestRecord { Method = context.Request.HttpMethod, Path = context.Request.RawUrl ?? "", Body = body, Authorization = context.Request.Headers["Authorization"] ?? "" });
                        Arrived.TrySetResult(true);
                        if (delay) await release.Task;
                        if (!listener.IsListening) break;
                        var bytes = binaryResponse != null && context.Request.Url!.AbsolutePath.EndsWith("/download", StringComparison.Ordinal) ? binaryResponse(context.Request) : Encoding.UTF8.GetBytes(response(context.Request));
                        context.Response.StatusCode = artworkStatus.HasValue && context.Request.Url!.AbsolutePath.Contains("/assets/") ? artworkStatus.Value : status;
                        context.Response.ContentType = "application/json";
                        if (status == 302) context.Response.RedirectLocation = "http://127.0.0.1:1/credential-target";
                        context.Response.ContentLength64 = bytes.Length;
                        await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                        context.Response.Close();
                    }
                }
                catch (Exception ex) when (!listener.IsListening || ex is HttpListenerException || ex is ObjectDisposedException || ex is IOException) { }
            });
        }
        public void Dispose() { listener.Close(); release.TrySetResult(true); worker.GetAwaiter().GetResult(); }
    }
    private sealed class TestLogger : ILogger
    {
        public List<string> Messages { get; } = new List<string>();
        public void Info(string message) { Messages.Add(message); }
        public void Info(Exception exception, string message) => Info(message);
        public void Debug(string message) => Info(message);
        public void Debug(Exception exception, string message) => Info(message);
        public void Warn(string message) => Info(message);
        public void Warn(Exception exception, string message) => Info(message);
        public void Error(string message) => Info(message);
        public void Error(Exception exception, string message) => Info(message);
        public void Trace(string message) => Info(message);
        public void Trace(Exception exception, string message) => Info(message);
    }
}
