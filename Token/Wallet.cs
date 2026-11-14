namespace BTokenCore;

internal class Wallet
{
  string PathFile;

  Dictionary<byte[], (string keyPrivateDecimal, byte[] keyPublic)> Keys = new(new EqualityComparerByteArray());
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

    Keys[Hash160KeyPublicReceiving] = (keyPrivateDecimal, KeyPublicReceiving);
  }

  internal bool IsKeyOwned(byte[] hash160)
  {
    return Keys.ContainsKey(hash160);
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

  internal byte[] FindHash160KeyFunding(Func<byte[], bool> canFund)
  {
    return Keys.Keys.FirstOrDefault(canFund);
  }

  internal byte[] GetKeyPublic(byte[] hash160)
  {
    return Keys[hash160].keyPublic;
  }

  internal byte[] GetSignature(byte[] hash160, byte[] dataToBeSigned)
  {
    return Crypto.GetSignature(Keys[hash160].keyPrivateDecimal, dataToBeSigned);
  }
}
