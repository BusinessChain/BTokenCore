namespace BTokenCore;

internal class Wallet
{
  string PathFile;

  Dictionary<byte[], (string keyPrivateDecimal, byte[] keyPublic)> Keys = new(new EqualityComparerByteArray());
  internal byte[] KeyPublicReceiving;
  internal byte[] Hash160KeyPublicReceiving;
  internal string AddressAccountReceiving;


  internal Wallet(string nameToken)
  {
    PathFile = $"Wallet{nameToken}/wallet";

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(PathFile)));

    if (!File.Exists(PathFile))
      File.WriteAllText(PathFile, "");

    foreach (string keyPrivateDecimal in File.ReadAllLines(PathFile).Select(l => l.Trim()).Where(l => l.Length > 0))
      SetKey(keyPrivateDecimal);

    if (Keys.Count == 0)
      AddKeys([Crypto.GenerateKeyPrivate()]);
  }

  internal void AddKeys(List<string> keysPrivateDecimal)
  {
    List<string> keysNew = keysPrivateDecimal
      .Where(k => !IsKeyOwned(Crypto.GetHash160KeyPublic(k)))
      .Distinct()
      .ToList();

    if (keysNew.Count == 0)
      return;

    keysNew.ForEach(SetKey);

    string content = File.ReadAllText(PathFile);
    string separator = content.Length == 0 || content.EndsWith('\n') ? "" : Environment.NewLine;

    File.AppendAllText(PathFile, separator + string.Join("", keysNew.Select(k => k + Environment.NewLine)));
  }


  void SetKey(string keyPrivateDecimal)
  {
    KeyPublicReceiving = Crypto.GetKeyPublic(keyPrivateDecimal);

    Hash160KeyPublicReceiving = Crypto.ComputeHash160(KeyPublicReceiving);

    AddressAccountReceiving = Hash160KeyPublicReceiving.BinaryToBase58Check();

    Keys[Hash160KeyPublicReceiving] = (keyPrivateDecimal, KeyPublicReceiving);
  }

  internal List<byte[]> GetHash160sKey()
  {
    return Keys.Keys.ToList();
  }

  internal List<string> GetKeysPrivate()
  {
    return Keys.Values.Select(k => k.keyPrivateDecimal).ToList();
  }

  internal bool IsKeyOwned(byte[] hash160)
  {
    return Keys.ContainsKey(hash160);
  }

  internal byte[] FindHash160Key(Func<byte[], bool> predicate)
  {
    return Keys.Keys.FirstOrDefault(predicate);
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
