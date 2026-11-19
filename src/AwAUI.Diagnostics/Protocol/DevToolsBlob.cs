using System;

namespace AwAUI.Diagnostics.Protocol;

/// <summary>
/// 二进制旁路数据（design/20：截图、图集、序列化的 DisplayList）。
/// </summary>
/// <remarks>
/// 线上约定：带 blob 的响应在信封中附加 <c>"blobs": [{ "blobId", "length", "mimeType" }]</c>，结果对象里用同一个
/// <c>blobId</c> 引用它；紧随这条 JSON 文本消息之后，按 <c>blobs</c> 数组的顺序各发送一个二进制消息（WebSocket 二进制帧）。
/// 响应与它的二进制消息通过 <see cref="Transport.DevToolsTransport.SendWithBlobsAsync"/> 作为整体发送，中间不会夹入任何其他消息
/// （事件、其他响应，包括客户端在接收回调里重入发出的请求的响应），客户端因此把响应之后的第 n 个二进制消息配给其 <c>blobs</c> 的第 n 项。
/// 进程内传输同样保证这一点（重入发送会排在当前这组消息之后投递）。
/// </remarks>
public sealed class DevToolsBlob
{
    internal DevToolsBlob(string id, ReadOnlyMemory<byte> data, string mimeType)
    {
        Id = id; Data = data; MimeType = mimeType;
    }

    /// <summary>会话内唯一的 blobId。</summary>
    public string Id { get; }
    public ReadOnlyMemory<byte> Data { get; }
    public string MimeType { get; }
}
