using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>
/// Changes a generated repository's settings after creation.
///
/// The generator owns a known, enumerable set of files. Everything else — business code, custom
/// skills, edited configuration — belongs to the developer and is never touched. That boundary is the
/// reason this command can exist at all: a template is not a package manager, and re-running
/// generation over a live repository would silently overwrite work.
///
/// Planning is the default. Nothing is written without --apply, and an --apply that fails restores
/// the files it changed from a backup taken in the same run. Git is never used to recover, because a
/// developer's uncommitted work is not the tool's to discard.
/// </summary>
internal static class Configure
{
    private const string ManifestPath = ".mpcore/template-manifest.json";
    private const int SupportedSchemaVersion = 4;

    private enum Kind { Add, Update, Delete }

    private sealed record Change(Kind Kind, string RelativePath, string? SourcePath, string Reason);

    public static async Task<int> RunAsync(IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("project", out var projectOption) || string.IsNullOrWhiteSpace(projectOption))
        {
            Console.Error.WriteLine("Missing required option --project.");
            PrintUsage();
            return 2;
        }

        var project = Path.GetFullPath(projectOption);
        var manifestFile = Path.Combine(project, ManifestPath);
        if (!File.Exists(manifestFile))
        {
            Console.Error.WriteLine($"Not an MP Core repository: {ManifestPath} not found under {project}.");
            return 3;
        }

        JsonNodeManifest manifest;
        try
        {
            manifest = JsonNodeManifest.Load(manifestFile);
        }
        catch (JsonException exception)
        {
            Console.Error.WriteLine($"Could not read {ManifestPath}: {exception.Message}");
            return 3;
        }

        if (manifest.SchemaVersion != SupportedSchemaVersion)
        {
            // An unrecognized schema must be refused rather than guessed: the members this command
            // relies on may mean something different.
            Console.Error.WriteLine(
                $"Manifest schema {manifest.SchemaVersion} is not supported by this CLI (expected {SupportedSchemaVersion}).");
            return 3;
        }

        var apply = options.ContainsKey("apply");
        var changes = new List<Change>();
        var manifestUpdates = new Dictionary<string, string>();
        var unsupported = new List<string>();

        // ---- settings this command refuses to migrate -------------------------------------------
        foreach (var (option, current, guidance) in new[]
                 {
                     ("shape", manifest.Get("shape"),
                         "Changing shape moves every bounded context between a modular monolith and a service boundary. "
                         + "It restructures projects, namespaces and registration, and no automated rewrite can know which "
                         + "of your types belong where."),
                     ("organization", manifest.Get("organization"),
                         "Renaming rewrites every namespace, assembly name, project file and using directive, and any "
                         + "published contract or deployment reference that already names them."),
                     ("component", manifest.Get("component"),
                         "Renaming rewrites every namespace, assembly name, project file and using directive, and any "
                         + "published contract or deployment reference that already names them."),
                 })
        {
            if (options.TryGetValue(option, out var requested) && requested != current)
            {
                unsupported.Add($"--{option}: {current} -> {requested}\n      {guidance}");
            }
        }

        if (options.TryGetValue("transport", out var requestedTransport) && requestedTransport != manifest.Get("transport"))
        {
            unsupported.Add(TransportGuidance(manifest.Get("transport"), requestedTransport));
        }

        if (options.TryGetValue("messaging", out var requestedMessaging) && requestedMessaging != manifest.Get("messaging"))
        {
            unsupported.Add(MessagingGuidance(manifest.Get("messaging"), requestedMessaging));
        }

        // ---- capabilities that rewrite generator-owned files: cache, business audit ---------------
        // Automated on the same terms as ai-tooling: the final state is materialized from the template,
        // every file that differs from the baseline is planned, and a file the developer has edited
        // stops the plan before anything is written.
        var currentCache = manifest.GetOrDefault("cache", "memory");
        var requestedCache = options.GetValueOrDefault("cache", currentCache);
        if (requestedCache != currentCache && requestedCache is not ("none" or "memory" or "redis" or "hybrid"))
        {
            Console.Error.WriteLine("--cache must be none, memory, redis, or hybrid.");
            return 2;
        }

        var currentAudit = manifest.GetOrDefault("businessAudit", "none");
        var requestedAudit = options.GetValueOrDefault("business-audit", currentAudit);
        if (requestedAudit != currentAudit && requestedAudit is not ("none" or "postgresql"))
        {
            Console.Error.WriteLine("--business-audit must be none or postgresql.");
            return 2;
        }

        var currentTimeseries = manifest.GetOrDefault("timeseries", "none");
        var requestedTimeseries = options.GetValueOrDefault("timeseries", currentTimeseries);
        if (requestedTimeseries != currentTimeseries && requestedTimeseries is not ("none" or "timescale"))
        {
            Console.Error.WriteLine("--timeseries must be none or timescale.");
            return 2;
        }

        var capabilityChanged = requestedCache != currentCache || requestedAudit != currentAudit || requestedTimeseries != currentTimeseries;

        // ---- runtime configuration: values only, never secrets -----------------------------------
        var appSettings = FindAppSettings(project);
        foreach (var (option, jsonPath, label) in new[]
                 {
                     ("security-authority", "Authority", "Security:Authority"),
                     ("security-audience", "Audiences", "Security:Audiences"),
                 })
        {
            if (!options.TryGetValue(option, out var value) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (LooksLikeSecret(value))
            {
                Console.Error.WriteLine(
                    $"--{option} looks like a credential. This command only sets non-secret values; "
                    + "use user secrets or environment variables for anything sensitive.");
                return 2;
            }

            if (appSettings is null)
            {
                Console.Error.WriteLine("Could not locate the host appsettings.json to update.");
                return 3;
            }

            var relative = Path.GetRelativePath(project, appSettings);
            if (SecurityValueMatches(appSettings, jsonPath, value))
            {
                continue;
            }

            changes.Add(new Change(Kind.Update, relative, null, $"set {label}"));
        }

        // ---- MP Core version --------------------------------------------------------------------
        if (options.TryGetValue("mpcore-version", out var requestedVersion)
            && requestedVersion != manifest.Get("mpcoreVersion"))
        {
            if (!MPCoreCli.IsExactSemanticVersion(requestedVersion))
            {
                Console.Error.WriteLine("--mpcore-version must be an exact semantic version; ranges and wildcards are prohibited.");
                return 2;
            }

            var props = Path.Combine(project, "Directory.Build.props");
            if (!File.Exists(props))
            {
                Console.Error.WriteLine("Directory.Build.props not found; cannot change the pinned MP Core version.");
                return 3;
            }

            changes.Add(new Change(Kind.Update, "Directory.Build.props", null,
                $"pin MP Core {manifest.Get("mpcoreVersion")} -> {requestedVersion}"));
            manifestUpdates["mpcoreVersion"] = requestedVersion;
        }

        // ---- AI tooling --------------------------------------------------------------------------
        string? materialized = null;
        string? baseline = null;
        string? final = null;
        var currentAi = manifest.Get("aiTooling");
        var requestedAi = options.GetValueOrDefault("ai-tooling", currentAi);
        var aiChanged = requestedAi != currentAi;
        var versionChanged = manifestUpdates.ContainsKey("mpcoreVersion");

        if (aiChanged && requestedAi is not ("both" or "codex" or "claude" or "none"))
        {
            Console.Error.WriteLine("--ai-tooling must be both, codex, claude, or none.");
            return 2;
        }

        if (aiChanged || versionChanged || capabilityChanged)
        {
            // The pristine output for the settings this repository currently records. Comparing against
            // it is the only reliable way to tell a file the generator owns from one the developer has
            // since edited: a name proves nothing about whether the content is still ours.
            baseline = await MaterializeTemplateAsync(manifest, currentAi, manifest.Get("mpcoreVersion"));
            if (baseline is null)
            {
                return 4;
            }
        }

        if (capabilityChanged)
        {
            // The complete requested state, so a combined request (say --cache with --mpcore-version)
            // plans each file once, with its final content.
            final = await MaterializeTemplateAsync(manifest, requestedAi,
                manifestUpdates.GetValueOrDefault("mpcoreVersion", manifest.Get("mpcoreVersion")),
                requestedCache, requestedAudit, requestedTimeseries);
            if (final is null)
            {
                Cleanup(baseline);
                Cleanup(final);
                return 4;
            }

            var reason = string.Join(", ",
                new[] { requestedCache != currentCache ? $"--cache {requestedCache}" : null, requestedAudit != currentAudit ? $"--business-audit {requestedAudit}" : null, requestedTimeseries != currentTimeseries ? $"--timeseries {requestedTimeseries}" : null }
                    .Where(r => r is not null));
            changes.AddRange(PlanCapabilitySwitch(project, baseline!, final, $"follows {reason}"));
            if (requestedCache != currentCache)
            {
                manifestUpdates["cache"] = requestedCache;
            }

            if (requestedAudit != currentAudit)
            {
                manifestUpdates["businessAudit"] = requestedAudit;
            }

            if (requestedTimeseries != currentTimeseries)
            {
                manifestUpdates["timeseries"] = requestedTimeseries;
            }
        }

        if (aiChanged)
        {
            materialized = await MaterializeTemplateAsync(manifest, requestedAi,
                manifestUpdates.GetValueOrDefault("mpcoreVersion", manifest.Get("mpcoreVersion")));
            if (materialized is null)
            {
                Cleanup(baseline);
                Cleanup(final);
                return 4;
            }
        }

        if (aiChanged)
        {
            changes.AddRange(PlanAiTooling(project, materialized, requestedAi));
            manifestUpdates["aiTooling"] = requestedAi;
        }

        if (versionChanged)
        {
            // Files that embed the version must move with it, or the repository documents a version it
            // no longer pins.
            var refreshed = materialized ?? await MaterializeTemplateAsync(manifest, currentAi,
                manifestUpdates["mpcoreVersion"]);
            if (refreshed is null)
            {
                Cleanup(baseline);
                Cleanup(final);
                return 4;
            }

            materialized ??= refreshed;
            foreach (var relative in new[]
                     {
                         "README.md", "docs/getting-started.md", "docs/ai-skills.md",
                         "docs/architecture.md", "docs/capabilities.md", ".mpcore/skills/BUNDLE.json",
                     })
            {
                var inProject = Path.Combine(project, relative);
                var inRefreshed = Path.Combine(refreshed, relative);
                if (!File.Exists(inProject) || !File.Exists(inRefreshed))
                {
                    continue;
                }

                if (changes.Any(c => c.RelativePath == relative)
                    || FilesMatch(inProject, inRefreshed))
                {
                    continue;
                }

                changes.Add(new Change(Kind.Update, relative, inRefreshed, "records the MP Core version"));
            }
        }

        // A file planned twice (capability switch and ai-tooling or version refresh) keeps its first
        // entry; every source is the same final content, so the choice is immaterial.
        var unique = changes.GroupBy(c => c.RelativePath, StringComparer.Ordinal).Select(g => g.First()).ToList();
        changes.Clear();
        changes.AddRange(unique);

        // ---- refuse to destroy work the developer has done to generator-owned files ---------------
        if (baseline is not null)
        {
            var conflicts = changes
                .Where(c => c.Kind != Kind.Add)
                .Where(c => c.SourcePath is not null || c.Kind == Kind.Delete)
                .Where(c => IsUserModified(project, baseline, c.RelativePath))
                .ToList();

            if (conflicts.Count > 0)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("Stopping: these files differ from what the generator produced, so they carry your edits:");
                foreach (var conflict in conflicts.OrderBy(c => c.RelativePath, StringComparer.Ordinal))
                {
                    var verb = conflict.Kind == Kind.Delete ? "would be deleted" : "would be overwritten";
                    Console.Error.WriteLine($"  - {conflict.RelativePath} ({verb})");
                }

                Console.Error.WriteLine();
                Console.Error.WriteLine("Nothing was changed, and nothing needs to be discarded.");
                Console.Error.WriteLine();
                Console.Error.WriteLine("These files are generated, so this command cannot merge your changes into a new");
                Console.Error.WriteLine("version of them without risking your intent. To keep both:");
                Console.Error.WriteLine();
                Console.Error.WriteLine("  1. Copy each file above somewhere safe, or commit it, so your version is recorded.");
                Console.Error.WriteLine("  2. Put lasting customisation where the generator does not manage it — a file of your");
                Console.Error.WriteLine("     own that AGENTS.md or CLAUDE.md references, or a skill whose directory name does");
                Console.Error.WriteLine("     not start with \"mpcore-\". Those are never touched.");
                Console.Error.WriteLine("  3. Re-run this command, then re-apply anything still needed on top of the new file.");
                Console.Error.WriteLine();
                Console.Error.WriteLine("There is deliberately no flag to overwrite them: a silent overwrite is exactly the");
                Console.Error.WriteLine("outcome this check exists to prevent.");
                Cleanup(materialized);
                Cleanup(baseline);
                Cleanup(final);
                return 7;
            }
        }

        if (unsupported.Count > 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Not supported as an automated change:");
            foreach (var item in unsupported)
            {
                Console.Error.WriteLine($"  - {item}");
            }

            Console.Error.WriteLine();
            Console.Error.WriteLine("Nothing was changed. Perform these manually, or generate a new repository and move your code across.");
            Cleanup(materialized);
            Cleanup(baseline);
            Cleanup(final);
            return 5;
        }

        if (changes.Count == 0)
        {
            Console.WriteLine("Already in the requested state. No changes.");
            Cleanup(materialized);
            Cleanup(baseline);
            Cleanup(final);
            return 0;
        }

        Console.WriteLine(apply ? "Applying:" : "Plan (nothing has been changed; add --apply to perform it):");
        foreach (var change in changes.OrderBy(c => c.RelativePath, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {change.Kind.ToString().ToLowerInvariant(),-6} {change.RelativePath}    ({change.Reason})");
        }

        Console.WriteLine();
        Console.WriteLine($"  {changes.Count(c => c.Kind == Kind.Add)} added, "
                          + $"{changes.Count(c => c.Kind == Kind.Update)} updated, "
                          + $"{changes.Count(c => c.Kind == Kind.Delete)} removed");

        if (!apply)
        {
            Console.WriteLine();
            Console.WriteLine("Files not listed above are never touched: business code, custom skills and your own edits.");
            Cleanup(materialized);
            Cleanup(baseline);
            Cleanup(final);
            return 0;
        }

        // Second resolution alone collides when two changes are applied back to back, and the second
        // run would overwrite the first run's only copy of the originals.
        var applied = new List<Change>();
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ");
        var backup = Path.Combine(project, ".mpcore", $"backup-{stamp}");
        for (var attempt = 2; Directory.Exists(backup); attempt++)
        {
            backup = Path.Combine(project, ".mpcore", $"backup-{stamp}-{attempt}");
        }
        try
        {
            Directory.CreateDirectory(backup);
            foreach (var change in changes.Where(c => c.Kind != Kind.Add))
            {
                var source = Path.Combine(project, change.RelativePath);
                if (!File.Exists(source))
                {
                    continue;
                }

                var destination = Path.Combine(backup, change.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
            }

            foreach (var change in changes)
            {
                Apply(project, change, options, appSettings);
                applied.Add(change);
            }

            foreach (var (key, value) in manifestUpdates)
            {
                manifest.Set(key, value);
            }

            // The manifest is written last and only after every file change succeeded, so it never
            // describes a state the repository is not actually in.
            manifest.Save(manifestFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"Failed: {exception.Message}");
            Console.Error.WriteLine("Undoing this run...");

            // Everything this run created is removed and everything it changed or deleted is put back,
            // so a failure halfway through leaves the repository as it was rather than in a state that
            // is neither the old one nor the new one.
            var removed = 0;
            foreach (var change in applied.Where(c => c.Kind == Kind.Add))
            {
                var added = Path.Combine(project, change.RelativePath);
                if (!File.Exists(added))
                {
                    continue;
                }

                File.Delete(added);
                removed++;
                PruneEmptyDirectories(project, Path.GetDirectoryName(added));
            }

            var restored = Restore(project, backup);
            try
            {
                Directory.Delete(backup, recursive: true);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Backup left at {Path.GetRelativePath(project, backup)} for inspection.");
            }

            Console.Error.WriteLine($"Removed {removed} file(s) this run added; restored {restored} file(s) it changed.");
            Console.Error.WriteLine("The manifest was not written, so it still describes the repository as it stands.");
            Cleanup(materialized);
            Cleanup(baseline);
            Cleanup(final);
            return 6;
        }

        Cleanup(materialized);
        Cleanup(baseline);
        Cleanup(final);
        Console.WriteLine();
        Console.WriteLine($"Done. Previous versions of changed files: {Path.GetRelativePath(project, backup)}");
        Console.WriteLine("Review the result, then delete that directory when you are satisfied.");
        return 0;
    }

    private static string TransportGuidance(string current, string requested)
    {
        var adding = requested == "both" && current is "grpc" or "rest";
        var detail = adding
            ? "Adding the second transport means new host wiring, a second Kestrel endpoint, port separation middleware "
              + "and a contract surface. The files are template-owned, but the endpoints that matter are yours: nothing "
              + "can generate the business contract, and a generated surface that exposes nothing is not progress."
            : "Removing a transport deletes the host surface your consumers may already call. No tool can know which "
              + "endpoints are still in use.";

        return $"--transport: {current} -> {requested}\n      {detail}\n"
               + "      Generate a scratch repository with the transport you want, diff its host against yours, and port "
               + "the wiring deliberately.";
    }

    private static string MessagingGuidance(string current, string requested)
    {
        var detail = current == "none"
            ? "Introducing a broker adds a package, host registration and connection configuration, and it changes how "
              + "failures behave. Topics, partitioning, retry and dead-letter handling are per-context decisions that a "
              + "generator cannot make for you."
            : "Switching or removing a broker changes delivery semantics for messages that may already be in flight.";

        return $"--messaging: {current} -> {requested}\n      {detail}\n"
               + "      Adding messaging is a deliberate design step; see docs/development-workflow.md.";
    }

    /// <summary>
    /// Every file whose generated content differs between the baseline (what the manifest records) and
    /// the final state. Files the template does not own — business code, custom skills — are absent
    /// from both and therefore never appear here.
    /// </summary>
    private static IEnumerable<Change> PlanCapabilitySwitch(string project, string baseline, string final, string reason)
    {
        static IEnumerable<string> Files(string root) => Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !f.StartsWith("bin/", StringComparison.Ordinal) && !f.StartsWith("obj/", StringComparison.Ordinal));

        var inBaseline = Files(baseline).ToHashSet(StringComparer.Ordinal);
        var inFinal = Files(final).ToHashSet(StringComparer.Ordinal);

        foreach (var relative in inFinal.Union(inBaseline).OrderBy(f => f, StringComparer.Ordinal))
        {
            var target = Path.Combine(final, relative);
            var reference = Path.Combine(baseline, relative);
            var existing = Path.Combine(project, relative);

            if (inFinal.Contains(relative) && inBaseline.Contains(relative))
            {
                if (FilesMatch(target, reference))
                {
                    continue;
                }

                // Unchanged by the developer, or already at the final content: either way not a conflict.
                if (File.Exists(existing) && FilesMatch(existing, target))
                {
                    continue;
                }

                yield return new Change(File.Exists(existing) ? Kind.Update : Kind.Add, relative, target, reason);
            }
            else if (inFinal.Contains(relative))
            {
                if (File.Exists(existing) && FilesMatch(existing, target))
                {
                    continue;
                }

                // An existing file the generator never produced is the developer's; planning it as an
                // update routes it through the conflict check, which stops rather than overwrites.
                yield return new Change(File.Exists(existing) ? Kind.Update : Kind.Add, relative, target, reason);
            }
            else if (File.Exists(existing))
            {
                yield return new Change(Kind.Delete, relative, null, $"not used after {reason}");
            }
        }
    }

    private static IEnumerable<Change> PlanAiTooling(string project, string? materialized, string requested)
    {
        var wantCodex = requested is "codex" or "both";
        var wantClaude = requested is "claude" or "both";
        var wantSkills = requested != "none";

        foreach (var (relative, wanted) in new[]
                 {
                     ("AGENTS.md", wantCodex),
                     ("CLAUDE.md", wantClaude),
                     ("docs/ai-skills.md", wantSkills),
                 })
        {
            var exists = File.Exists(Path.Combine(project, relative));
            if (wanted && materialized is not null && File.Exists(Path.Combine(materialized, relative)))
            {
                yield return new Change(exists ? Kind.Update : Kind.Add, relative,
                    Path.Combine(materialized, relative), "generator-owned entry point");
            }
            else if (!wanted && exists)
            {
                yield return new Change(Kind.Delete, relative, null, "not used by the requested ai-tooling");
            }
        }

        if (materialized is not null)
        {
            foreach (var guide in new[]
                     {
                         "README.md", "docs/getting-started.md", "docs/architecture.md", "docs/capabilities.md",
                     })
            {
                var inProject = Path.Combine(project, guide);
                var inTarget = Path.Combine(materialized, guide);
                if (File.Exists(inProject) && File.Exists(inTarget) && !FilesMatch(inProject, inTarget))
                {
                    yield return new Change(Kind.Update, guide, inTarget, "documents the assistant tooling");
                }
            }
        }

        foreach (var (root, wanted) in new[]
                 {
                     (".agents/skills", wantCodex),
                     (".claude/skills", wantClaude),
                     (".mpcore/skills", wantSkills),
                 })
        {
            foreach (var change in PlanSkillRoot(project, materialized, root, wanted))
            {
                yield return change;
            }
        }
    }

    private static IEnumerable<Change> PlanSkillRoot(string project, string? materialized, string root, bool wanted)
    {
        var projectRoot = Path.Combine(project, root);

        if (wanted && materialized is not null)
        {
            var sourceRoot = Path.Combine(materialized, root);
            if (!Directory.Exists(sourceRoot))
            {
                yield break;
            }

            foreach (var file in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Path.Combine(root, Path.GetRelativePath(sourceRoot, file));
                var exists = File.Exists(Path.Combine(project, relative));
                yield return new Change(exists ? Kind.Update : Kind.Add, relative, file, "generator-owned skill file");
            }

            yield break;
        }

        if (!Directory.Exists(projectRoot))
        {
            yield break;
        }

        // Only generator-owned skills are removed. A skill the developer wrote does not start with the
        // generator's prefix, and deleting it because a flag changed would be destroying their work.
        foreach (var directory in Directory.GetDirectories(projectRoot))
        {
            var name = Path.GetFileName(directory);
            if (!name.StartsWith("mpcore-", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            {
                yield return new Change(Kind.Delete, Path.GetRelativePath(project, file), null, "generator-owned skill file");
            }
        }

        foreach (var file in new[] { "INVENTORY.md", "BUNDLE.json" })
        {
            var path = Path.Combine(projectRoot, file);
            if (File.Exists(path))
            {
                yield return new Change(Kind.Delete, Path.GetRelativePath(project, path), null, "generator-owned index");
            }
        }
    }

    private static void Apply(string project, Change change, IReadOnlyDictionary<string, string> options, string? appSettings)
    {
        var target = Path.Combine(project, change.RelativePath);

        switch (change.Kind)
        {
            case Kind.Delete:
                File.Delete(target);
                // Prune directories the removal emptied, up to but never including the project root.
                // Leaving an empty .agents behind would advertise a tooling layout the project no
                // longer has, and the next reader cannot tell it is vestigial.
                PruneEmptyDirectories(project, Path.GetDirectoryName(target));
                break;

            case Kind.Add:
            case Kind.Update when change.SourcePath is not null:
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(change.SourcePath!, target, overwrite: true);
                break;

            case Kind.Update when change.RelativePath == "Directory.Build.props":
                var version = options["mpcore-version"];
                var props = File.ReadAllText(target);
                File.WriteAllText(target, Regex.Replace(props,
                    "<MPCoreVersion>[^<]*</MPCoreVersion>", $"<MPCoreVersion>{version}</MPCoreVersion>"));
                break;

            case Kind.Update when appSettings is not null:
                var json = File.ReadAllText(appSettings);
                if (options.TryGetValue("security-authority", out var authority))
                {
                    json = Regex.Replace(json, "(\"Authority\"\\s*:\\s*\")[^\"]*(\")", $"$1{authority}$2");
                }

                if (options.TryGetValue("security-audience", out var audience))
                {
                    json = Regex.Replace(json, "(\"Audiences\"\\s*:\\s*\\[\\s*\")[^\"]*(\")", $"$1{audience}$2");
                }

                File.WriteAllText(appSettings, json);
                break;
        }
    }

    private static void PruneEmptyDirectories(string project, string? directory)
    {
        var root = Path.GetFullPath(project).TrimEnd(Path.DirectorySeparatorChar);
        while (directory is not null
               && Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) != root
               && Directory.Exists(directory)
               && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory);
        }
    }

    private static int Restore(string project, string backup)
    {
        if (!Directory.Exists(backup))
        {
            return 0;
        }

        var restored = 0;
        foreach (var file in Directory.GetFiles(backup, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(backup, file);
            var destination = Path.Combine(project, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            restored++;
        }

        return restored;
    }

    /// <summary>
    /// Produces the generator's current output for the requested settings in a temporary directory, so
    /// the skills and entry points come from the installed template rather than from a copy embedded in
    /// this tool. The template/CLI version gate applies here exactly as it does during generation.
    /// </summary>
    private static async Task<string?> MaterializeTemplateAsync(JsonNodeManifest manifest, string aiTooling, string mpcoreVersion,
        string? cache = null, string? businessAudit = null, string? timeseries = null)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"mpcore-configure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);

        var exit = await MPCoreCli.RunProcessQuietAsync("dotnet",
        [
            "new", "mpcore-backend",
            "--name", manifest.Get("productName"),
            "--output", temp,
            "--organization", manifest.Get("organization"),
            "--component", manifest.Get("component"),
            "--shape", manifest.Get("shape"),
            "--messaging", manifest.Get("messaging"),
            "--transport", manifest.Get("transport"),
            "--businessAudit", businessAudit ?? manifest.GetOrDefault("businessAudit", "none"),
            "--cache", cache ?? manifest.GetOrDefault("cache", "memory"),
            "--timeseries", timeseries ?? manifest.GetOrDefault("timeseries", "none"),
            "--aiTooling", aiTooling,
            "--mpcoreVersion", mpcoreVersion
        ], Directory.GetCurrentDirectory());

        if (exit != 0)
        {
            Console.Error.WriteLine("Could not run the template. Install the matching MPCore.Templates package first.");
            Cleanup(temp);
            return null;
        }

        // The reference must be the output of the template this repository was generated from. Comparing
        // against a newer installed template would mark every unedited file as modified, and a stale one
        // would mark edited files as pristine — the first is noise, the second silently destroys work.
        var required = manifest.Get("templateVersion");
        var marker = Path.Combine(temp, MPCoreCli.TemplateVersionMarkerFileName);
        var installed = File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
        if (string.IsNullOrEmpty(required) || installed != required)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                $"This repository was generated with MPCore.Templates {(string.IsNullOrEmpty(required) ? "an unrecorded version" : required)}, "
                + $"but the installed template is {installed ?? "unknown"}.");
            Console.Error.WriteLine(
                "Without the matching template there is no reliable way to tell your edits from generated");
            Console.Error.WriteLine("content, so nothing was changed.");
            if (!string.IsNullOrEmpty(required))
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("  dotnet new uninstall MPCore.Templates");
                Console.Error.WriteLine($"  dotnet new install MPCore.Templates::{required}");
            }

            Cleanup(temp);
            return null;
        }

        File.Delete(marker);
        return temp;
    }

    /// <summary>
    /// True when the file in the project no longer matches the generator's own output for the settings
    /// the manifest records. A missing baseline counterpart means the generator never produced it, so
    /// it is the developer's file either way.
    /// </summary>
    private static bool IsUserModified(string project, string baseline, string relativePath)
    {
        var current = Path.Combine(project, relativePath);
        if (!File.Exists(current))
        {
            return false;
        }

        var pristine = Path.Combine(baseline, relativePath);
        return !File.Exists(pristine) || !FilesMatch(current, pristine);
    }

    private static bool FilesMatch(string left, string right)
    {
        var a = new FileInfo(left);
        var b = new FileInfo(right);
        return a.Length == b.Length && File.ReadAllBytes(left).AsSpan().SequenceEqual(File.ReadAllBytes(right));
    }

    private static void Cleanup(string? directory)
    {
        if (directory is null || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary directory is harmless; failing the command over it is not.
        }
    }

    private static string? FindAppSettings(string project)
    {
        var src = Path.Combine(project, "src");
        return Directory.Exists(src)
            ? Directory.GetFiles(src, "appsettings.json", SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }

    private static bool SecurityValueMatches(string appSettings, string key, string value)
    {
        var json = File.ReadAllText(appSettings);
        var match = key == "Authority"
            ? Regex.Match(json, "\"Authority\"\\s*:\\s*\"([^\"]*)\"")
            : Regex.Match(json, "\"Audiences\"\\s*:\\s*\\[\\s*\"([^\"]*)\"");
        return match.Success && match.Groups[1].Value == value;
    }

    private static bool LooksLikeSecret(string value) =>
        value.Contains("password", StringComparison.OrdinalIgnoreCase)
        || value.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || value.Contains("apikey", StringComparison.OrdinalIgnoreCase)
        || value.Contains("api-key", StringComparison.OrdinalIgnoreCase);

    public static void PrintUsage() => Console.WriteLine(
        """
        mpcore configure --project <path> [changes] [--apply]

          Plans by default and changes nothing. Add --apply to perform the plan.

          --ai-tooling both|codex|claude|none   add or remove assistant entry points and skills
          --cache none|memory|redis|hybrid      switch the cache adapter and its wiring
          --business-audit none|postgresql      add or remove the business audit trail wiring
          --timeseries none|timescale           add or remove the TimescaleDB migration helpers
          --mpcore-version <exact-version>      change the pinned MP Core version
          --security-authority <url>            set Security:Authority (non-secret values only)
          --security-audience <audience>        set Security:Audiences (non-secret values only)

          Not automated, reported with guidance: --shape, --transport, --messaging,
          --organization, --component.
        """);
}


/// <summary>
/// Minimal read/modify/write over the generation manifest that preserves every member it does not
/// change, including ones a newer CLI may not know about.
/// </summary>
internal sealed class JsonNodeManifest
{
    private readonly JsonObject _root;

    private JsonNodeManifest(JsonObject root) => _root = root;

    public int SchemaVersion => _root["schemaVersion"]?.GetValue<int>() ?? -1;

    /// <summary>Reads a field that later cohorts added, so an older manifest keeps working.</summary>
    public string GetOrDefault(string name, string fallback) => _root[name]?.GetValue<string>() ?? fallback;

    public static JsonNodeManifest Load(string path)
    {
        var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                   ?? throw new JsonException("manifest is not a JSON object");
        return new JsonNodeManifest(node);
    }

    public string Get(string key) => _root[key]?.GetValue<string>() ?? string.Empty;

    public void Set(string key, string value) => _root[key] = value;

    public void Save(string path) =>
        File.WriteAllText(path, _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}
