namespace BTokenCore;

internal class Wallet
{
  string PathFile;

  Dictionary<byte[], string> KeysPrivateDecimal = new(new EqualityComparerByteArray());
  internal byte[] KeyPublicReceiving;
  internal byte[] Hash160KeyPublicReceiving;
  internal string AddressAccountReceiving;


  internal Wallet(string pathFile)
  {
    PathFile = pathFile;

    foreach (string keyPrivateDecimal in File.ReadAllLines(PathFile).Select(l => l.Trim()).Where(l => l.Length > 0))
      SetKey(keyPrivateDecimal);
  }

  void SetKey(string keyPrivateDecimal)
  {
    KeyPublicReceiving = Crypto.GetKeyPublic(keyPrivateDecimal);

    Hash160KeyPublicReceiving = Crypto.ComputeHash160(KeyPublicReceiving);

    AddressAccountReceiving = Hash160KeyPublicReceiving.BinaryToBase58Check();

    KeysPrivateDecimal[KeyPublicReceiving] = keyPrivateDecimal;
  }

  internal byte[] GetHash160KeyReceiving(Func<byte[], bool> canReceive)
  {
    while (!canReceive(Hash160KeyPublicReceiving))
    {
      string keyPrivateDecimal = Crypto.GenerateKeyPrivate();

      SetKey(keyPrivateDecimal);

      string separator = File.ReadAllText(PathFile).EndsWith('\n') ? "" : Environment.NewLine;

      File.AppendAllText(PathFile, separator + keyPrivateDecimal + Environment.NewLine);
    }

    return Hash160KeyPublicReceiving;
  }

  internal byte[] GetSignature(byte[] keyPublic, byte[] dataToBeSigned)
  {
    return Crypto.GetSignature(KeysPrivateDecimal[keyPublic], dataToBeSigned);
  }
}
