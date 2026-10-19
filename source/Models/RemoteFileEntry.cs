namespace StreamZilla.Models;

public class RemoteFileEntry : FileEntry
{
    public string Owner { get; set; } = "";
    public string Group { get; set; } = "";
    public string Permissions { get; set; } = "";
}
