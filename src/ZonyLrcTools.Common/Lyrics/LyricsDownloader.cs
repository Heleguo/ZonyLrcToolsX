﻿using System.Text;
using Microsoft.Extensions.Options;
using Polly;
using ZonyLrcTools.Common.Configuration;
using ZonyLrcTools.Common.Infrastructure.DependencyInject;
using ZonyLrcTools.Common.Infrastructure.Exceptions;
using ZonyLrcTools.Common.Infrastructure.Extensions;
using ZonyLrcTools.Common.Infrastructure.Logging;
using ZonyLrcTools.Common.Infrastructure.Threading;

namespace ZonyLrcTools.Common.Lyrics;

public class LyricsDownloader : ILyricsDownloader, ISingletonDependency
{
    private readonly IEnumerable<ILyricsProvider> _lyricsProviders;
    private readonly IWarpLogger _logger;
    private readonly GlobalOptions _options;

    public IEnumerable<ILyricsProvider> AvailableProviders => new Lazy<IEnumerable<ILyricsProvider>>(() =>
    {
        return _options.Provider.Lyric.Plugin
            .Where(op => op.Priority != -1)
            .OrderBy(op => op.Priority)
            .Join(_lyricsProviders,
                op => op.Name,
                loader => loader.DownloaderName,
                (_, loader) => loader);
    }).Value;

    public LyricsDownloader(IEnumerable<ILyricsProvider> lyricsProviders,
        IOptions<GlobalOptions> options,
        IWarpLogger logger)
    {
        _lyricsProviders = lyricsProviders;
        _logger = logger;
        _options = options.Value;
    }

    public async Task DownloadAsync(List<MusicInfo> needDownloadMusicInfos,
        int parallelCount = 1,
        CancellationToken cancellationToken = default)
    {
        await _logger.InfoAsync("开始下载歌词文件数据...");

        if (parallelCount <= 0)
        {
            parallelCount = 1;
        }

        var warpTask = new WarpTask(parallelCount);
        var downloadTasks = needDownloadMusicInfos.Select(info =>
            warpTask.RunAsync(() =>
                Task.Run(async () =>
                {
                    // Try to download lyrics from all available providers.
                    foreach (var lyricsProvider in AvailableProviders)
                    {
                        await DownloadAndWriteLyricsAsync(lyricsProvider, info);

                        if (info.IsSuccessful)
                        {
                            _logger.LogSuccessful(info);
                            return;
                        }
                    }
                }, cancellationToken), cancellationToken));

        await Task.WhenAll(downloadTasks);

        await _logger.InfoAsync($"歌词数据下载完成，成功: {needDownloadMusicInfos.Count(m => m.IsSuccessful)} 失败{needDownloadMusicInfos.Count(m => m.IsSuccessful == false)}。");
        await LogFailedSongFilesInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"歌词下载失败列表_{DateTime.Now:yyyyMMddHHmmss}.txt"), needDownloadMusicInfos);
    }

    private async Task DownloadAndWriteLyricsAsync(ILyricsProvider provider, MusicInfo info)
    {
        try
        {
            var lyrics = await provider.DownloadAsync(info.Name, info.Artist);

            if (lyrics.IsPruneMusic)
            {
                info.IsSuccessful = true;
                return;
            }

            var newLyricsFilePath = Path.Combine(Path.GetDirectoryName(info.FilePath)!,
                $"{Path.GetFileNameWithoutExtension(info.FilePath)}.lrc");

            if (File.Exists(newLyricsFilePath))
            {
                File.Delete(newLyricsFilePath);
            }

            // Write lyrics to file.
            await using (var fileStream = new FileStream(newLyricsFilePath, FileMode.CreateNew, FileAccess.Write))
            {
                await using (var binaryWriter = new BinaryWriter(fileStream, Encoding.UTF8))
                {
                    binaryWriter.Write(Utf8ToSelectedEncoding(lyrics));
                    binaryWriter.Flush();
                }
            }

            info.IsSuccessful = true;
        }
        catch (ErrorCodeException ex)
        {
            _logger.LogWarningInfo(ex);
            info.IsSuccessful = false;
        }
        catch (Exception ex)
        {
            await _logger.ErrorAsync($"下载歌词文件时发生错误：{ex.Message}，歌曲名: {info.Name}，歌手: {info.Artist}。");
            info.IsSuccessful = false;
        }
    }

    // Convert UTF-8 to selected encoding.
    private byte[] Utf8ToSelectedEncoding(LyricsItemCollection lyrics)
    {
        var supportEncodings = Encoding.GetEncodings();

        // If the encoding is UTF-8 BOM, add the BOM to the front of the lyrics.
        if (_options.Provider.Lyric.Config.FileEncoding == "utf-8-bom")
        {
            return Encoding.UTF8.GetPreamble().Concat(lyrics.GetUtf8Bytes()).ToArray();
        }

        if (supportEncodings.All(x => x.Name != _options.Provider.Lyric.Config.FileEncoding))
        {
            throw new ErrorCodeException(ErrorCodes.NotSupportedFileEncoding);
        }

        return Encoding.Convert(Encoding.UTF8, Encoding.GetEncoding(_options.Provider.Lyric.Config.FileEncoding), lyrics.GetUtf8Bytes());
    }

    private async Task LogFailedSongFilesInfo(string outFilePath, List<MusicInfo> musicInfos)
    {
        var failedSongFiles = new StringBuilder();
        failedSongFiles.AppendLine("歌曲名,歌手,路径");

        foreach (var failedSongFile in musicInfos.Where(m => m.IsSuccessful == false))
        {
            failedSongFiles.AppendLine($"{failedSongFile.Name},{failedSongFile.Artist},{failedSongFile.FilePath}");
        }

        await File.WriteAllTextAsync(outFilePath, failedSongFiles.ToString());
    }
}