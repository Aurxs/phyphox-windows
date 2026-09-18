// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using Phyphox.Storage;

namespace Phyphox.Server;
public static class StorageEndpoints
{
    public static void MapStorageEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/snapshots", (SessionService session) => session.ListSnapshots());
        app.MapGet("/api/v1/recordings", (SessionService session) => session.ListRecordings());
        app.MapPost("/api/v1/recordings/{id}/replay", (string id, SessionService session, CancellationToken ct) => session.ReplayRecording(id, ct));
        app.MapPost("/api/v1/snapshots/save", (SaveSnapshotRequest request, SessionService session, CancellationToken ct) => session.SaveSnapshotAsync(request.Id, ct));
        app.MapPost("/api/v1/snapshots/{id}/restore", (string id, SessionService session, CancellationToken ct) => session.RestoreSnapshotAsync(id, ct));
        app.MapPost("/api/v1/imports/data", async (HttpRequest request, SessionService session, CancellationToken ct) =>
        {
            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? throw new InvalidOperationException("请选择数据文件。");
            if (file.Length == 0 || file.Length > LibraryService.MaximumImportBytes)
                throw new InvalidOperationException("数据文件大小无效。");
            var optionsText = form["optionsJSON"].ToString();
            if (string.IsNullOrWhiteSpace(optionsText))
                throw new InvalidOperationException("需要 optionsJSON 指定明确的数据映射。");
            var options = JsonSerializer.Deserialize<DataImportRequestOptions>(optionsText, StorageJson.Options) ?? throw new InvalidOperationException("无效的数据映射。");
            await using var source = file.OpenReadStream();
            using var bytes = new MemoryStream();
            await source.CopyToAsync(bytes, ct);
            return await session.ImportDataAsync(bytes.ToArray(), file.FileName, options, ct);
        });
    }

    public sealed record SaveSnapshotRequest(string? Id = null);
}
