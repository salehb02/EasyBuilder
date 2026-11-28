using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
#if EASYBUILDER_AI_ASSISTANT
using Unity.AI.Assistant.Agents;
using Unity.AI.Assistant.Editor.Api;
#endif

namespace EasyBuilder
{
    /// <summary>An AI that can write the changelog notes.</summary>
    internal abstract class ChangelogAI
    {
        /// <summary>Saved in the settings to remember the choice.</summary>
        public abstract string Id { get; }

        public abstract string DisplayName { get; }

        public abstract bool IsAvailable();

        /// <summary>How to install it, shown when it isn't installed.</summary>
        public abstract string InstallHint { get; }

        /// <summary>Sends one prompt and returns the answer. Must stop when <paramref name="cancellationToken"/> is canceled.</summary>
        public abstract Task<string> AskAsync(string systemPrompt, string prompt, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Writes changelog notes from a range of git commits (by default since the last tag), with Unity's AI Assistant or an AI
    /// command-line tool installed on this machine (Claude Code, Codex, Gemini).
    /// </summary>
    internal static class ChangelogGenerator
    {
        public const int DefaultTimeoutSeconds = 120;
        public const int MinTimeoutSeconds = 10;
        public const int MaxTimeoutSeconds = 1800;

        private const int MaxCommits = 200;
        private const int MaxCommitText = 15000;
        private const int GitTimeoutMs = 15000;

        /// <summary>Every AI Easy Builder knows how to use, installed or not, in the order they are listed.</summary>
        public static readonly IReadOnlyList<ChangelogAI> All = new ChangelogAI[]
        {
            new UnityAssistantAI(),
            // No tools and no MCP servers: the commits are in the prompt, so it only needs to write.
            new CommandLineAI("claude-code", "Claude Code", "claude",
                "-p --output-format text --tools \"\" --no-session-persistence --strict-mcp-config",
                "Install Claude Code (https://claude.com/claude-code) and run 'claude' once in a terminal to sign in."),
            new CommandLineAI("codex", "Codex CLI", "codex", "exec --skip-git-repo-check --sandbox read-only --color never -",
                "Install it with 'npm install -g @openai/codex' and run 'codex' once in a terminal to sign in."),
            new CommandLineAI("gemini", "Gemini CLI", "gemini", "",
                "Install it with 'npm install -g @google/gemini-cli' and run 'gemini' once in a terminal to sign in."),
        };

        /// <summary>
        /// The AI with this id, installed or not, so a choice isn't lost while its tool is missing. Without a saved
        /// choice: the first installed AI, or the first one listed.
        /// </summary>
        public static ChangelogAI Resolve(string id) =>
            All.FirstOrDefault(ai => ai.Id == id) ?? All.FirstOrDefault(ai => ai.IsAvailable()) ?? All[0];

        /// <exception cref="TimeoutException">The AI didn't answer within <paramref name="timeoutSeconds"/>.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
        /// <param name="from">The first commit, tag or branch included. Empty: the commits after the newest tag before <paramref name="to"/>.</param>
        /// <param name="to">The last commit, tag or branch included. Empty: HEAD.</param>
        public static async Task<string> GenerateAsync(ChangelogAI ai, ChangelogDetail detail, int timeoutSeconds,
            string from = null, string to = null, CancellationToken cancellationToken = default)
        {
            var commits = GetCommits(from, to, out var range);

            if (string.IsNullOrWhiteSpace(commits))
                throw new InvalidOperationException($"There are no commits {range} to write a changelog from.");

            var prompt = $"Detail level: {detail}.\n{DetailInstructions(detail)}\n\nCommits {range}, newest first:\n\n{commits}";
            timeoutSeconds = Mathf.Clamp(timeoutSeconds, MinTimeoutSeconds, MaxTimeoutSeconds);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);

            var ask = ai.AskAsync(SystemPrompt, prompt, linked.Token);

            // Also stop waiting if the AI ignores the token.
            var finished = await Task.WhenAny(ask, Task.Delay(Timeout.Infinite, linked.Token));

            if (finished != ask)
                _ = ask.ContinueWith(task => task.Exception, TaskContinuationOptions.OnlyOnFaulted); // Don't leave it unobserved.

            string answer;

            try
            {
                answer = await (finished == ask ? ask : Task.FromCanceled<string>(linked.Token));
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"{ai.DisplayName} didn't answer within {timeoutSeconds} seconds. " +
                                           "Try again, or raise the AI timeout.");
            }

            if (string.IsNullOrWhiteSpace(answer))
                throw new InvalidOperationException($"{ai.DisplayName} returned an empty answer.");

            var notes = Clean(answer);

            if (notes.Trim('.', ' ').Equals("NONE", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"No changes worth listing at the {detail} detail level in the commits {range}." +
                                                    (detail < ChangelogDetail.Detailed ? " Try the Detailed level." : ""));

            return notes;
        }

        private const string SystemPrompt =
            "You write release notes for a game build from git commit messages.\n" +
            "Answer with the notes only, one change per line, in the form `type(scope): description` or `type: description`.\n" +
            "Use only these types: feat, fix, perf, refactor, docs, chore. Add `!` after the type for breaking changes.\n" +
            "Write short descriptions a player or tester understands, e.g. `fix: Crash when opening the shop`.\n" +
            "Merge commits that describe the same change, skip merge commits and version bumps, and never invent changes.\n" +
            "To add detail to a change, put it on the next line, indented by two spaces.\n" +
            "If no commit is worth listing at the requested detail level, answer with exactly NONE.\n" +
            "No headings, no introduction, no closing remarks, no code fences.";

        private static string DetailInstructions(ChangelogDetail detail) => detail switch
        {
            ChangelogDetail.Brief => "Only list changes players notice (feat and fix). At most 8 lines, no indented detail lines.",
            ChangelogDetail.Detailed => "List every meaningful change, including docs and chore. Add an indented detail line where it helps.",
            _ => "List features, fixes, performance work and notable refactors, one line each. Leave out docs and chore.",
        };

        // Models sometimes add remarks or code fences anyway. Keep the "type: text" lines and the indented
        // detail lines under them; if there are none, return the answer as it is (e.g. "NONE").
        private static string Clean(string answer)
        {
            var kept = new List<string>();
            var keepingDetails = false;

            foreach (var line in answer.Replace("\r\n", "\n").Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (char.IsWhiteSpace(line[0]) && keepingDetails)
                {
                    kept.Add(line.TrimEnd());
                    continue;
                }

                keepingDetails = Changelog.IsTypedLine(line);

                if (keepingDetails)
                    kept.Add(line.Trim());
            }

            return Changelog.Normalize(kept.Count > 0 ? string.Join("\n", kept) : answer);
        }

        // No whitespace or quotes (they'd break the git arguments), and no leading '-' (it'd be read as an option).
        private static readonly Regex RevisionPattern = new(@"^[^\s""'\\-][^\s""'\\]*$");

        /// <summary>
        /// Commit messages from <paramref name="from"/> to <paramref name="to"/>, both included. Empty
        /// <paramref name="to"/> means HEAD. Empty <paramref name="from"/> means the commits after the newest tag before
        /// <paramref name="to"/> (the tagged commit belongs to the previous release, so it's left out; a tag on
        /// <paramref name="to"/> itself doesn't count, so a just-tagged release gets its own notes), or the latest
        /// <see cref="MaxCommits"/> when there's no tag.
        /// </summary>
        public static string GetCommits(string from, string to, out string range)
        {
            if (RunGit("rev-parse --is-inside-work-tree", out _) != 0)
                throw new InvalidOperationException("The project isn't in a git repository, so there are no commits to read.");

            from = from?.Trim();
            to = to?.Trim();

            var end = string.IsNullOrEmpty(to) ? "HEAD" : VerifyRevision(to, "To");
            var fromIncluded = !string.IsNullOrEmpty(from);
            var start = fromIncluded
                ? VerifyRevision(from, "From")
                : RunGit($"describe --tags --abbrev=0 \"{end}^\"", out var tagOutput) == 0 ? tagOutput.Trim() : null;

            range = start == null ? $"up to {end} (the last {MaxCommits}, no tag found)"
                : fromIncluded ? $"from {start} to {end}, both included"
                : $"after {start} up to {end}";

            // "^start^@" leaves out start's parents but keeps start itself, and works for the first commit too.
            var revision = start == null ? $"\"{end}\""
                : fromIncluded ? $"\"{end}\" \"^{start}^@\""
                : $"\"{start}..{end}\"";

            // %x1e separates commits, since bodies can contain blank lines.
            if (RunGit($"log {revision} --no-merges -n {MaxCommits} --pretty=format:%s%n%b%x1e", out var log) != 0)
                throw new InvalidOperationException("git log failed: " + log.Trim());

            var commits = log.Split('\x1e')
                .Select(commit => Regex.Replace(commit.Trim(), @"\n{2,}", "\n"))
                .Where(commit => commit.Length > 0)
                .Select(commit => "- " + commit.Replace("\n", "\n  "));

            var text = string.Join("\n", commits);

            // Strictly older: both checks pass when they are the same commit.
            if (text.Length == 0 && start != null && RunGit($"merge-base --is-ancestor \"{end}\" \"{start}\"", out _) == 0 &&
                RunGit($"merge-base --is-ancestor \"{start}\" \"{end}\"", out _) != 0)
                throw new InvalidOperationException($"'{start}' comes after '{end}'. Swap From and To.");

            return text.Length > MaxCommitText ? text.Substring(0, MaxCommitText) + "\n(older commits left out)" : text;
        }

        private static string VerifyRevision(string revision, string field)
        {
            if (!RevisionPattern.IsMatch(revision))
                throw new InvalidOperationException($"{field}: '{revision}' isn't a valid commit, tag or branch name.");

            if (RunGit($"rev-parse --verify --quiet \"{revision}^{{commit}}\"", out _) != 0)
                throw new InvalidOperationException($"{field}: there's no commit, tag or branch '{revision}' in this repository.");

            return revision;
        }

        /// <summary>The newest tags and commits, to pick a range from. Empty lists when git can't be read.</summary>
        public static (List<string> tags, List<(string hash, string subject)> commits) GetRecentRevisions(int count = 25)
        {
            var tags = new List<string>();
            var commits = new List<(string, string)>();

            try
            {
                if (RunGit($"for-each-ref --sort=-creatordate --count={count} --format=%(refname:short) refs/tags", out var tagOutput) == 0)
                    tags.AddRange(tagOutput.Split('\n').Select(tag => tag.Trim()).Where(tag => tag.Length > 0));

                if (RunGit($"log -n {count} --pretty=format:%h%x09%s", out var logOutput) == 0)
                    foreach (var line in logOutput.Split('\n'))
                    {
                        var tab = line.IndexOf('\t');

                        if (tab > 0)
                            commits.Add((line.Substring(0, tab).Trim(), line.Substring(tab + 1).Trim()));
                    }
            }
            catch (InvalidOperationException)
            {
                // Not a repository, or git is missing; the caller shows an empty menu.
            }

            return (tags, commits);
        }

        private static int RunGit(string arguments, out string output)
        {
            var startInfo = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = Changelog.ProjectFolder,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            try
            {
                using var process = Process.Start(startInfo);
                var error = process.StandardError.ReadToEndAsync();
                var standardOutput = process.StandardOutput.ReadToEndAsync();

                if (!process.WaitForExit(GitTimeoutMs))
                {
                    CommandLineAI.KillTree(process);
                    throw new InvalidOperationException($"git {arguments} didn't finish within {GitTimeoutMs / 1000} seconds.");
                }

                output = process.ExitCode == 0 ? standardOutput.Result : error.Result;
                return process.ExitCode;
            }
            catch (Win32Exception)
            {
                throw new InvalidOperationException("Git isn't installed or isn't on the PATH.");
            }
        }
    }

    /// <summary>Unity's AI Assistant package (com.unity.ai.assistant), through its headless API.</summary>
    internal sealed class UnityAssistantAI : ChangelogAI
    {
        public override string Id => "unity-assistant";
        public override string DisplayName => "Unity AI Assistant";

        public override string InstallHint =>
            "Install the Assistant package (com.unity.ai.assistant 2.19 or newer) with the Package Manager and sign in to Unity.";

        public override bool IsAvailable() =>
#if EASYBUILDER_AI_ASSISTANT
            true;
#else
            false;
#endif

        public override async Task<string> AskAsync(string systemPrompt, string prompt, CancellationToken cancellationToken)
        {
#if EASYBUILDER_AI_ASSISTANT
            var agent = new LlmAgent()
                .WithId("easybuilder_changelog")
                .WithName("Easy Builder Changelog")
                .WithDescription("Writes release notes for a game build from git commit messages.")
                .WithSystemPrompt(systemPrompt);

            return await agent.RunHeadless(prompt, cancellationToken: cancellationToken);
#else
            await Task.CompletedTask;
            throw new NotSupportedException("Unity's AI Assistant package (com.unity.ai.assistant) isn't installed.");
#endif
        }
    }

    /// <summary>
    /// An AI command-line tool, run once in print mode with the prompt on standard input. It runs in the temp
    /// folder, not the project, so it can't read or change project files.
    /// </summary>
    internal sealed class CommandLineAI : ChangelogAI
    {
        private readonly string _command;
        private readonly string _arguments;

        public CommandLineAI(string id, string displayName, string command, string arguments, string installHint)
        {
            Id = id;
            DisplayName = displayName;
            _command = command;
            _arguments = arguments;
            InstallHint = installHint;
        }

        public override string Id { get; }
        public override string DisplayName { get; }
        public override string InstallHint { get; }

        public override bool IsAvailable() => FindExecutable(_command) != null;

        public override async Task<string> AskAsync(string systemPrompt, string prompt, CancellationToken cancellationToken)
        {
            var executable = FindExecutable(_command)
                             ?? throw new InvalidOperationException($"{DisplayName} ('{_command}') wasn't found on this machine.");

            // npm installs Windows tools as .cmd scripts, which only cmd.exe can run.
            var isScript = executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                           executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

            var startInfo = isScript
                ? new ProcessStartInfo("cmd.exe", $"/d /s /c \"\"{executable}\" {_arguments}\"")
                : new ProcessStartInfo(executable, _arguments);

            startInfo.WorkingDirectory = Path.GetTempPath();
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;
            // The tools often live next to node, which a Unity started from the Dock or Hub may not have on its PATH.
            startInfo.EnvironmentVariables["PATH"] = string.Join(Path.PathSeparator.ToString(), SearchFolders());

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += (_, _) => exited.TrySetResult(true);

            try
            {
                process.Start();
            }
            catch (Win32Exception e)
            {
                throw new InvalidOperationException($"Couldn't start {DisplayName}: {e.Message}");
            }

            using (cancellationToken.Register(() => KillTree(process)))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();

                try
                {
                    var input = new UTF8Encoding(false).GetBytes(systemPrompt + "\n\n" + prompt);
                    await process.StandardInput.BaseStream.WriteAsync(input, 0, input.Length, cancellationToken);
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                    // It exited before reading everything; its error output says why.
                }

                if (process.HasExited)
                    exited.TrySetResult(true);

                await exited.Task;
                cancellationToken.ThrowIfCancellationRequested();

                var answer = await output;

                if (process.ExitCode != 0)
                {
                    var message = (await error).Trim();
                    message = message.Length > 500 ? message.Substring(0, 500) + "…" : message;
                    throw new InvalidOperationException($"{DisplayName} failed (exit code {process.ExitCode}). " +
                                                        (message.Length > 0 ? message : "Is it signed in? Run it once in a terminal to check."));
                }

                return answer;
            }
        }

        /// <summary>Stops the process and, on Windows, the processes it started (e.g. node behind a .cmd script).</summary>
        internal static void KillTree(Process process)
        {
            try
            {
                if (process.HasExited)
                    return;

                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    using var taskKill = Process.Start(new ProcessStartInfo("taskkill", $"/T /F /PID {process.Id}")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                    taskKill?.WaitForExit(5000);
                }

                if (!process.HasExited)
                    process.Kill();
            }
            catch (Exception e) when (e is InvalidOperationException || e is Win32Exception)
            {
                // It already exited.
            }
        }

        private static string FindExecutable(string command)
        {
            var extensions = Application.platform == RuntimePlatform.WindowsEditor
                ? new[] { ".exe", ".cmd", ".bat" }
                : new[] { "" };

            foreach (var folder in SearchFolders())
            foreach (var extension in extensions)
            {
                try
                {
                    var path = Path.Combine(folder, command + extension);

                    if (File.Exists(path))
                        return path;
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry.
                }
            }

            return null;
        }

        // PATH, plus the usual install folders, which Unity's PATH often lacks (especially on macOS).
        private static IEnumerable<string> SearchFolders()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

            var extra = new List<string>
            {
                Path.Combine(home, ".local", "bin"),
                Path.Combine(home, ".claude", "local"),
                Path.Combine(home, ".npm-global", "bin"),
                Path.Combine(home, ".bun", "bin"),
                Path.Combine(home, ".volta", "bin"),
            };

            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                extra.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
            }
            else
            {
                extra.Add("/opt/homebrew/bin");
                extra.Add("/usr/local/bin");
            }

            return path.Split(Path.PathSeparator)
                .Concat(extra)
                .Where(folder => !string.IsNullOrWhiteSpace(folder))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }
    }
}
