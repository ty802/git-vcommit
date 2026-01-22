using System.Diagnostics;
using LibGit2Sharp;

namespace gittool;

public class Program
{
    class Options
    {
        public string TargetBranch { get; set; } = "";
        public string? Message { get; set; }
        public bool Verbose { get; set; }
        public bool DryRun { get; set; }
        public bool? GpgSign { get; set; } // null = use config, true/false = override
    }

    static Options ParseArgs(string[] args)
    {
        var opts = new Options();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-m":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("error: option '-m' requires a value");
                        Environment.Exit(1);
                    }
                    opts.Message = args[++i];
                    break;
                case "-v":
                case "--verbose":
                    opts.Verbose = true;
                    break;
                case "--dry-run":
                    opts.DryRun = true;
                    break;
                case "-S":
                case "--gpg-sign":
                    opts.GpgSign = true;
                    break;
                case "--no-gpg-sign":
                    opts.GpgSign = false;
                    break;
                default:
                    if (args[i].StartsWith("-"))
                    {
                        Console.Error.WriteLine($"error: unknown option: {args[i]}");
                        Environment.Exit(1);
                    }
                    opts.TargetBranch = args[i];
                    break;
            }
        }

        if (string.IsNullOrEmpty(opts.TargetBranch))
        {
            Console.Error.WriteLine("usage: git-vcommit [options] <branch>");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Options:");
            Console.Error.WriteLine("  -m <message>      Use given message (skip editor)");
            Console.Error.WriteLine("  -v, --verbose     Show diff in commit message template");
            Console.Error.WriteLine("  --dry-run         Show what would be committed without committing");
            Console.Error.WriteLine("  -S, --gpg-sign    GPG sign the commit (overrides config)");
            Console.Error.WriteLine("  --no-gpg-sign     Don't GPG sign (overrides config)");
            Environment.Exit(1);
        }

        return opts;
    }

    static Signature GetAuthorSignature(Repository repo)
    {
        string? name = Environment.GetEnvironmentVariable("GIT_AUTHOR_NAME")
            ?? repo.Config.Get<string>("user.name")?.Value;

        string? email = Environment.GetEnvironmentVariable("GIT_AUTHOR_EMAIL")
            ?? repo.Config.Get<string>("user.email")?.Value;

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(email))
        {
            Console.Error.WriteLine("fatal: unable to auto-detect email address");
            Console.Error.WriteLine();
            Console.Error.WriteLine("*** Please tell me who you are.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Run");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  git config user.name \"Your Name\"");
            Console.Error.WriteLine("  git config user.email \"you@example.com\"");
            Console.Error.WriteLine();
            Console.Error.WriteLine("to set your account's default identity.");
            Environment.Exit(1);
        }

        var when = DateTimeOffset.Now;
        return new Signature(name, email, when);
    }

    static Signature GetCommitterSignature(Repository repo)
    {
        string? name = Environment.GetEnvironmentVariable("GIT_COMMITTER_NAME")
            ?? Environment.GetEnvironmentVariable("GIT_AUTHOR_NAME")
            ?? repo.Config.Get<string>("user.name")?.Value;

        string? email = Environment.GetEnvironmentVariable("GIT_COMMITTER_EMAIL")
            ?? Environment.GetEnvironmentVariable("GIT_AUTHOR_EMAIL")
            ?? repo.Config.Get<string>("user.email")?.Value;

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(email))
        {
            Console.Error.WriteLine("fatal: unable to auto-detect email address");
            Environment.Exit(1);
        }

        var when = DateTimeOffset.Now;
        return new Signature(name, email, when);
    }

    static TreeChanges GetStagedChanges(Repository repo)
    {
        return repo.Diff.Compare<TreeChanges>(
            repo.Head.Tip?.Tree,
            DiffTargets.Index
        );
    }

    static Tree BuildTreeWithStagedChanges(Repository repo, Commit targetCommit, TreeChanges stagedChanges)
    {
        // Start with target branch's tree
        var treeDefinition = TreeDefinition.From(targetCommit.Tree);

        // Apply staged changes to the target tree
        foreach (var change in stagedChanges)
        {
            switch (change.Status)
            {
                case ChangeKind.Added:
                case ChangeKind.Modified:
                    // Get the staged blob from the index
                    var indexEntry = repo.Index[change.Path];
                    if (indexEntry != null)
                    {
                        treeDefinition.Add(change.Path, indexEntry.Id, indexEntry.Mode);
                    }
                    break;
                    
                case ChangeKind.Deleted:
                    treeDefinition.Remove(change.Path);
                    break;
                    
                case ChangeKind.Renamed:
                    treeDefinition.Remove(change.OldPath);
                    var renamedEntry = repo.Index[change.Path];
                    if (renamedEntry != null)
                    {
                        treeDefinition.Add(change.Path, renamedEntry.Id, renamedEntry.Mode);
                    }
                    break;
            }
        }

        return repo.ObjectDatabase.CreateTree(treeDefinition);
    }

    static string GetCommitMessage(Repository repo, Options opts, TreeChanges changes)
    {
        // If -m flag provided, use it
        if (!string.IsNullOrEmpty(opts.Message))
            return opts.Message;

        // Otherwise, open editor
        string tempFile = Path.GetTempFileName();

        using (var writer = new StreamWriter(tempFile))
        {
            writer.WriteLine();
            writer.WriteLine("# Please enter the commit message for your changes. Lines starting");
            writer.WriteLine("# with '#' will be ignored, and an empty message aborts the commit.");
            writer.WriteLine("#");
            writer.WriteLine($"# Changes to be committed (on branch '{opts.TargetBranch}'):");

            foreach (var change in changes)
            {
                string status = change.Status switch
                {
                    ChangeKind.Added => "new file",
                    ChangeKind.Modified => "modified",
                    ChangeKind.Deleted => "deleted",
                    ChangeKind.Renamed => "renamed",
                    _ => "changed"
                };
                
                string path = change.Status == ChangeKind.Renamed 
                    ? $"{change.OldPath} -> {change.Path}" 
                    : change.Path;
                    
                writer.WriteLine($"#\t{status}:   {path}");
            }

            // If -v flag, show diff
            if (opts.Verbose)
            {
                writer.WriteLine("#");
                writer.WriteLine("# ------------------------ >8 ------------------------");
                writer.WriteLine("# Do not modify or remove the line above.");
                writer.WriteLine("# Everything below it will be ignored.");

                var patch = repo.Diff.Compare<Patch>(
                    repo.Head.Tip?.Tree,
                    DiffTargets.Index
                );
                
                foreach (var line in patch.Content.Split('\n'))
                {
                    writer.WriteLine($"# {line}");
                }
            }
        }

        // Open editor
        string editor = Environment.GetEnvironmentVariable("EDITOR")
            ?? Environment.GetEnvironmentVariable("VISUAL")
            ?? "nano";

        var psi = new ProcessStartInfo(editor, tempFile)
        {
            UseShellExecute = false
        };

        var process = Process.Start(psi);
        process?.WaitForExit();

        if (process?.ExitCode != 0)
        {
            File.Delete(tempFile);
            Console.Error.WriteLine("fatal: editor exited with error");
            Environment.Exit(1);
        }

        // Read and clean message
        string message = File.ReadAllText(tempFile);
        File.Delete(tempFile);

        return CleanCommitMessage(message);
    }

    static string CleanCommitMessage(string raw)
    {
        var lines = raw.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("#"))
            .Select(line => line.TrimEnd());

        string cleaned = string.Join("\n", lines).Trim();

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            Console.Error.WriteLine("Aborting commit due to empty commit message.");
            Environment.Exit(1);
        }

        return cleaned;
    }

    static bool RunHook(string hookName, string? messageFilePath = null)
    {
        string hookPath = Path.Combine(".git", "hooks", hookName);

        // Check if hook exists
        if (!File.Exists(hookPath))
            return true;

        // Make sure it's executable on Unix
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                var mode = File.GetUnixFileMode(hookPath);
                if (!mode.HasFlag(UnixFileMode.UserExecute))
                {
                    Console.Error.WriteLine($"hint: The '{hookName}' hook was ignored because it's not set as executable.");
                    Console.Error.WriteLine($"hint: You can disable this warning with `git config advice.ignoredHook false`.");
                    return true; // Not fatal, just skip
                }
            }
            catch
            {
                return true; // If we can't check, just skip
            }
        }

        var args = messageFilePath != null ? new[] { messageFilePath } : Array.Empty<string>();
        var psi = new ProcessStartInfo(hookPath, args)
        {
            WorkingDirectory = ".",
            UseShellExecute = false
        };

        var process = Process.Start(psi);
        if (process == null)
            return false;

        process.WaitForExit();
        return process.ExitCode == 0;
    }

    static bool ShouldGpgSign(Repository repo, Options opts)
    {
        // Command-line flag takes precedence
        if (opts.GpgSign.HasValue)
            return opts.GpgSign.Value;

        // Fall back to config
        return repo.Config.Get<bool>("commit.gpgsign")?.Value ?? false;
    }

    static Commit CreateCommit(Repository repo, Tree tree, Commit parent, string message, bool gpgSign, Signature author, Signature committer)
    {
        if (gpgSign)
        {
            // Use git commit-tree for GPG signing (hybrid approach)
            string messageFile = Path.GetTempFileName();
            File.WriteAllText(messageFile, message);

            var psi = new ProcessStartInfo("git",
                new[] { "commit-tree", tree.Sha, "-p", parent.Sha, "-F", messageFile, "-S" })
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            // Set author/committer environment
            psi.Environment["GIT_AUTHOR_NAME"] = author.Name;
            psi.Environment["GIT_AUTHOR_EMAIL"] = author.Email;
            psi.Environment["GIT_AUTHOR_DATE"] = author.When.ToString("yyyy-MM-dd HH:mm:ss zzz");
            psi.Environment["GIT_COMMITTER_NAME"] = committer.Name;
            psi.Environment["GIT_COMMITTER_EMAIL"] = committer.Email;
            psi.Environment["GIT_COMMITTER_DATE"] = committer.When.ToString("yyyy-MM-dd HH:mm:ss zzz");

            var process = Process.Start(psi);
            if (process == null)
            {
                File.Delete(messageFile);
                Console.Error.WriteLine("fatal: failed to start git commit-tree process");
                Environment.Exit(1);
            }

            string? commitSha = process.StandardOutput.ReadLine();
            string? errors = process.StandardError.ReadToEnd();
            process.WaitForExit();
            File.Delete(messageFile);

            if (process.ExitCode != 0 || string.IsNullOrEmpty(commitSha))
            {
                if (!string.IsNullOrEmpty(errors))
                    Console.Error.Write(errors);
                Console.Error.WriteLine("fatal: failed to create signed commit");
                Environment.Exit(1);
            }

            return repo.Lookup<Commit>(commitSha)!;
        }
        else
        {
            return repo.ObjectDatabase.CreateCommit(
                author,
                committer,
                message,
                tree,
                new[] { parent },
                prettifyMessage: false
            );
        }
    }

    static void UpdateBranch(Repository repo, string branchName, Commit commit)
    {
        string refName = $"refs/heads/{branchName}";
        var reference = repo.Refs[refName];

        string reflogMessage = $"commit: {commit.MessageShort}";
        repo.Refs.UpdateTarget(reference, commit.Id, reflogMessage);

        // Output similar to git commit
        // Note: Stats calculation disabled for AOT compatibility
        var parents = commit.Parents.ToList();
        if (parents.Count == 0)
        {
            Console.WriteLine($"[{branchName} (root-commit) {commit.Sha.Substring(0, 7)}] {commit.MessageShort}");
        }
        else
        {
            Console.WriteLine($"[{branchName} {commit.Sha.Substring(0, 7)}] {commit.MessageShort}");
        }
    }

    static void ShowDryRun(Options opts, TreeChanges changes, Commit targetCommit)
    {
        Console.WriteLine($"On branch {opts.TargetBranch}");
        Console.WriteLine("Changes to be committed:");
        Console.WriteLine();

        foreach (var change in changes)
        {
            string status = change.Status switch
            {
                ChangeKind.Added => "new file",
                ChangeKind.Modified => "modified",
                ChangeKind.Deleted => "deleted",
                ChangeKind.Renamed => "renamed",
                _ => "changed"
            };

            string path = change.Status == ChangeKind.Renamed
                ? $"{change.OldPath} -> {change.Path}"
                : change.Path;

            Console.WriteLine($"\t{status}:   {path}");
        }

        Console.WriteLine();
        Console.WriteLine($"Would create commit on branch '{opts.TargetBranch}'");
        Console.WriteLine($"Parent commit: {targetCommit.Sha.Substring(0, 7)} {targetCommit.MessageShort}");
    }

    public static void Main(string[] args)
    {
        try
        {
            // 1. Parse arguments
            var opts = ParseArgs(args);

            // 2. Open repository
            using var repo = new Repository(".");

            // 3. Validate branch exists
            string refName = $"refs/heads/{opts.TargetBranch}";
            if (repo.Refs[refName] == null)
            {
                Console.Error.WriteLine($"fatal: ref '{opts.TargetBranch}' not found");
                Environment.Exit(1);
            }

            var targetCommit = repo.Branches[opts.TargetBranch].Tip;

            // 4. Check staged changes
            var changes = GetStagedChanges(repo);
            if (!changes.Any())
            {
                Console.WriteLine("nothing to commit");
                return;
            }

            // 5. Dry run mode - exit early
            if (opts.DryRun)
            {
                ShowDryRun(opts, changes, targetCommit);
                return;
            }

            // 6. Validate identity before any writes
            var author = GetAuthorSignature(repo);
            var committer = GetCommitterSignature(repo);

            // 7. Build new tree
            var newTree = BuildTreeWithStagedChanges(repo, targetCommit, changes);

            // 8. Get commit message (editor or -m flag)
            string message = GetCommitMessage(repo, opts, changes);

            // 9. Run commit-msg hook (can modify message)
            string messageTempFile = Path.GetTempFileName();
            File.WriteAllText(messageTempFile, message);

            if (!RunHook("commit-msg", messageTempFile))
            {
                File.Delete(messageTempFile);
                Console.Error.WriteLine("fatal: commit-msg hook failed");
                Environment.Exit(1);
            }

            // Read potentially modified message
            message = File.ReadAllText(messageTempFile);
            File.Delete(messageTempFile);

            // 10. Determine GPG signing
            bool shouldSign = ShouldGpgSign(repo, opts);

            // 11. Create commit
            var newCommit = CreateCommit(repo, newTree, targetCommit, message, shouldSign, author, committer);

            // 12. Update branch reference
            UpdateBranch(repo, opts.TargetBranch, newCommit);

            // 13. Run post-commit hook (notifications only)
            try
            {
                RunHook("post-commit");
            }
            catch
            {
                // Ignore post-commit hook errors (AOT compatibility)
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"fatal: {ex.Message}");
            Environment.Exit(1);
        }
    }
}
