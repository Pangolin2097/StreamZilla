using System.Threading;
using System.Threading.Tasks;
using StreamZilla.Models;

namespace StreamZilla.Services
{
    public interface IParallelTransferService
    {
        Task DownloadAsync(
            AppSettings settings,
            RemoteEntry remoteFile,
            string localPath,
            int streams,
            TransferEntry entry,
            CancellationToken cancellationToken);

        Task UploadAsync(
            AppSettings settings,
            string localPath,
            string remotePath,
            int streams,
            TransferEntry entry,
            CancellationToken cancellationToken);
    }
}
