using Lantern.Discv5.Enr.EnrContent;
using Lantern.Discv5.Enr.EnrContent.Entries;
using Lantern.Discv5.Enr.IdentityScheme.Interfaces;

namespace Lantern.Discv5.Enr;

public class EnrBuilder
{
    private Dictionary<string, IContentEntry> _entries = new();
    private IIdentitySchemeVerifier _verifier;
    private IIdentitySchemeSigner _signer;

    public EnrBuilder WithIdentityScheme(IIdentitySchemeVerifier verifier, IIdentitySchemeSigner signer)
    {
        _verifier = verifier;
        _signer = signer;
        return this;
    }

    public EnrBuilder WithEntry(string key, IContentEntry? entry)
    {
        if(entry != null)
            _entries[key] = entry;
        return this;
    }

    public EnrRecord Build()
    {
        if (_signer == null)
        {
            throw new InvalidOperationException("Signer must be set before building the EnrRecord.");
        }

        var enrRecord = new EnrRecord(_entries,_verifier, _signer);
        
        enrRecord.UpdateSignature();
        
        return enrRecord;
    }
}