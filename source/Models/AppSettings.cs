namespace StreamZilla.Models;

public class AppSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string LastLocalPath { get; set; } = "";
    public int ParallelConnections { get; set; } = 8;
    public int MaxConcurrentDownloads { get; set; } = 4;

    public List<string> ExpandedLocalPaths { get; set; } = new List<string>();
    public string LastLocalFolderPath { get; set; } = "";

    public Dictionary<string, List<string>> ExpandedRemotePathsByConnection { get; set; } = new Dictionary<string, List<string>>();
    public string LastRemoteSelectedPath { get; set; } = "";

    public Dictionary<string, string> KnownHostKeys { get; set; } = new Dictionary<string, string>();
}
