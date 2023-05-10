namespace Lantern.Discv5.WireProtocol.Session;

public class SharedKeys
{
    public SharedKeys(byte[] keyData)
    {
        InitiatorKey = keyData[..16];
        RecipientKey = keyData[16..];
    }
    
    public readonly byte[] InitiatorKey;
    
    public readonly byte[] RecipientKey;
}