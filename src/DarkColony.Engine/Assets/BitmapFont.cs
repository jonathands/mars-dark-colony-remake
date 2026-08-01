namespace DarkColony.Engine.Assets;

/// <summary>Character mapping and metrics for a Dark Colony SPR bitmap font.</summary>
public sealed class BitmapFont
{
    public BitmapFont(Sprite sprite, int frameOffset, int lineHeight)
    {
        Sprite = sprite;
        FrameOffset = frameOffset;
        LineHeight = lineHeight;
    }

    public Sprite Sprite { get; }
    public int FrameOffset { get; }
    public int LineHeight { get; }

    public int FrameIndex(char character) => character - FrameOffset;

    public SpriteFrame? Glyph(char character)
    {
        var index = FrameIndex(character);
        return index >= 0 && index < Sprite.Frames.Count ? Sprite.Frames[index] : null;
    }

    public int Advance(char character)
    {
        var glyph = Glyph(character);
        if (glyph is null) return 0;
        return glyph.Width == 0 ? glyph.AnchorX : glyph.AnchorX + glyph.Width;
    }

    public int Measure(string text) => text.Sum(character => Advance(character));
}
