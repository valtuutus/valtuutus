using Valtuutus.Core.Data;

namespace Valtuutus.Core.Engines;

public class SnapTokenUtils
{
    public static async Task LoadLatestSnapToken(IDataReaderProvider reader, IWithSnapToken req, CancellationToken cancellationToken)
    {
        if (req.SnapToken is null)
        {
            var latest = await reader.GetLatestSnapToken(cancellationToken);
            req.SnapToken = latest;
        }
    }
}