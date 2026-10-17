using System;

namespace StreamZilla.Models;

public abstract class Entry
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime? Created { get; set; }
    public DateTime? Modified { get; set; }
}
