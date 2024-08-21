using Valtuutus.Core.Data;

namespace Valtuutus.Core.Engines;

public interface IWithSnapToken
{
    public SnapToken? SnapToken { get; set; }
}