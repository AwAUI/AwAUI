namespace AwAUI.Graphics;

/// <summary>
/// DisplayList 操作码（design/13）。数值是二进制格式的一部分，发布后不可改变；
/// 尚未实现的操作码保留编号。
/// </summary>
public enum DrawOp : byte
{
    None = 0,

    // 状态
    Save = 1,
    Restore = 2,
    Translate = 3,
    Transform = 4,
    ClipRect = 5,
    ClipRoundRect = 6,
    ClipPath = 7,

    // UI 高频图元
    FillRect = 16,
    FillRoundRect = 17,
    StrokeRect = 18,
    StrokeRoundRect = 19,
    DrawBorder = 20,            // 保留：CSS 边框（M1）
    DrawBoxShadow = 21,
    DrawLine = 22,

    // 通用
    FillPath = 32,
    StrokePath = 33,
    DrawImage = 34,             // DrawImageCmd，Bitmap 在资源表中
    DrawImageNine = 35,         // 保留
    DrawGlyphRun = 36,          // DrawGlyphRunCmd，GlyphRun 在资源表中
    DrawTextDecoration = 37,    // 保留

    // 分组
    PushLayer = 48,
    PopLayer = 49,
    DrawDisplayList = 50,
    DrawCustom = 51,            // 保留

    // 元数据
    Annotation = 64,
}

/// <summary>命令头中的标志位。</summary>
[System.Flags]
public enum DrawFlags : byte
{
    None = 0,
    AntiAlias = 1,
}
