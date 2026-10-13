using System;

namespace StreamZilla.Models;

public class RemoteEntry
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public string Owner { get; set; } = "";
    public string Permissions { get; set; } = "";
    public DateTime? LastModified { get; set; }
    public DateTime? Created { get; set; }
}
