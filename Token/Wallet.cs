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

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(PathFile)));

    if (!File.Exists(PathFile))
      File.WriteAllText(PathFile, "");

    foreach (string keyPrivateDecimal in File.ReadAllLines(PathFile).Select(l => l.Trim()).Where(l => l.Length > 0))
      SetKey(keyPrivateDecimal);

    if (Keys.Count == 0)
      AddKeyGenerated();
  }

  void AddKeyGenerated()
  {
    string keyPrivateDecimal = Crypto.GenerateKeyPrivate();

    SetKey(keyPrivateDecimal);

    string content = File.ReadAllText(PathFile);
    string separator = content.Length == 0 || content.EndsWith('\n') ? "" : Environment.NewLine;

    File.AppendAllText(PathFile, separator + keyPrivateDecimal + Environment.NewLine);
  }

  void SetKey(string keyPrivateDecimal)
  {
    KeyPublicReceiving = Crypto.GetKeyPublic(keyPrivateDecimal);

    Hash160KeyPublicReceiving = Crypto.ComputeHash160(KeyPublicReceiving);

    AddressAccountReceiving = Hash160KeyPublicReceiving.BinaryToBase58Check();

    Keys[Hash160KeyPublicReceiving] = (keyPrivateDecimal, KeyPublicReceiving);
  }

  internal List<string> GetKeysPrivate()
  {
    return Keys.Values.Select(k => k.keyPrivateDecimal).ToList();
  }

  internal bool IsKeyOwned(byte[] hash160)
  {
    return Keys.ContainsKey(hash160);
  }

  internal byte[] GetHash160KeyReceiving(Func<byte[], bool> canReceive)
  {
    while (!canReceive(Hash160KeyPublicReceiving))
      AddKeyGenerated();

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
