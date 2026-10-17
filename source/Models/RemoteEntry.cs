using System;

namespace StreamZilla.Models;

public sealed class RemoteEntry : Entry
{
    public string Owner { get; set; } = "";
    public string Group { get; set; } = "";
    public string Permissions { get; set; } = "";
}
