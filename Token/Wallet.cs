namespace BTokenCore;

internal class Wallet
{
  string PathFile;

  internal string KeyPrivateDecimal;
  internal byte[] KeyPublic;
  internal byte[] Hash160PKeyPublic;
  internal string AddressAccount;


  internal Wallet(string pathFile)
  {
    PathFile = pathFile;

    SetKey(File.ReadAllLines(PathFile).Select(l => l.Trim()).Last(l => l.Length > 0));
  }

  void SetKey(string privKeyDec)
  {
    KeyPrivateDecimal = privKeyDec;

    KeyPublic = Crypto.GetKeyPublic(KeyPrivateDecimal);

    Hash160PKeyPublic = Crypto.ComputeHash160(KeyPublic);

    AddressAccount = Hash160PKeyPublic.BinaryToBase58Check();
  }

  internal byte[] GetHash160KeyReceiving(Func<byte[], bool> canReceive)
  {
    while (!canReceive(Hash160PKeyPublic))
    {
      SetKey(Crypto.GenerateKeyPrivate());

      string separator = File.ReadAllText(PathFile).EndsWith('\n') ? "" : Environment.NewLine;

      File.AppendAllText(PathFile, separator + KeyPrivateDecimal + Environment.NewLine);
    }

    return Hash160PKeyPublic;
  }

  internal byte[] GetSignature(byte[] dataToBeSigned)
  {
    return Crypto.GetSignature(KeyPrivateDecimal, dataToBeSigned);
  }
}
