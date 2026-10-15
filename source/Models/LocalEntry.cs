using System;

namespace StreamZilla.Models;

public class LocalEntry
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDrive { get; set; }
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime Created { get; set; }
    public DateTime Modified { get; set; }
}
