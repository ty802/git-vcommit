using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.Arm;
using System.Text;
using System.Text.RegularExpressions;
using System.Transactions;
namespace gittool;
public class Program
{
    private static string mktmp()
    {
        ProcessStartInfo procstart = new("mktemp") { RedirectStandardOutput = true };
        Process? process = Process.Start(procstart);
        if (process is null) Environment.Exit(1);
        string? tmp = process.StandardOutput?.ReadLine();
        if (tmp is null) Environment.Exit(1);
        return tmp;
    }
    public static void Main(string[] args)
    {
        string targetname = args[0];
        string tmp = mktmp();
        ProcessStartInfo procstart;
        procstart = new("git", new string[] { "rev-parse", "HEAD" });
        procstart.RedirectStandardOutput = true;
        Process? process;
        process = Process.Start(procstart);
        if (process is null) Environment.Exit(1);
        process.WaitForExit();
        string? sha = process.StandardOutput?.ReadLine() ?? null;
        if (sha is null) Environment.Exit(1);
        procstart = new("git", new string[] { "diff-index", "--cached", sha });
        procstart.RedirectStandardOutput = true;
        process = Process.Start(procstart);
        if (process is null) Environment.Exit(1);
        process.WaitForExit();
        StreamReader? sr = process.StandardOutput;
        if (sr is null) Environment.Exit(1);
        Regex rx = new Regex(@"(?:^|\n).(?:\d{6}) (\d{6}) (?:[a-z0-9]{40}) ([a-z0-9]{40}) ([A-Z])\s+(.*)");
        string res1 = sr.ReadToEnd(); ;
        MatchCollection cache = rx.Matches(res1);
        procstart = new("git", new string[] { "read-tree", targetname });
        procstart.Environment.Add("GIT_INDEX_FILE", tmp);
        Process.Start(procstart)?.WaitForExit();
        string[] gargs;
        gargs = new string[6] { "update-index", "--add", "--cacheinfo", null, null, null };
        foreach (Match m in cache)
        {
            gargs[3] = m.Groups[1].Value;
            gargs[4] = m.Groups[2].Value;
            gargs[5] = m.Groups[4].Value;
            procstart = new("git", gargs);
            Console.WriteLine(string.Join(" ", gargs));
            procstart.Environment.Add("GIT_INDEX_FILE", tmp);
            Process.Start(procstart)?.WaitForExit();
        }
        procstart = new("git", new string[] { "write-tree" }) { RedirectStandardOutput = true };
        procstart.Environment.Add("GIT_INDEX_FILE", tmp);
        process = Process.Start(procstart);
        if (process is null) Environment.Exit(1);
        StreamReader? sr2 = process.StandardOutput;
        if (sr2 is null) Environment.Exit(1);
        string commitmessage = mktmp();
        procstart = new(Environment.GetEnvironmentVariable("EDITOR") ?? "nano", new string[] { commitmessage });
        process = Process.Start(procstart);
        if (process is null) Environment.Exit(1);
        process.WaitForExit();
        string? treeish = sr2.ReadLine();
        Console.WriteLine(treeish);
        Console.WriteLine(args[0]);
        procstart = new("git", new string[] { "commit-tree", treeish ?? "", "-p", targetname, "-F", commitmessage, "-S" }){RedirectStandardOutput = true};
        procstart.Environment.Add("GIT_INDEX_FILE", tmp);
        process = Process.Start(procstart);
        if (process is null) Environment.Exit(1);
        process.WaitForExit();
        if (process.ExitCode == 0 && process.StandardOutput is StreamReader sr3 && sr3.ReadLine() is string commitish)
        {
            Console.WriteLine(commitish);
            procstart = new("git", new string[] { "branch","-f",targetname, commitish});
            procstart.Environment.Add("GIT_INDEX_FILE", tmp);
            Process.Start(procstart);
        }
    }
}