namespace KakaoTalkFilterLab.Models;

public sealed record WindowInfo(
    nint Handle,
    string Title,
    string ClassName,
    int X,
    int Y,
    int Width,
    int Height)
{
    public int Area => Width * Height;
}
