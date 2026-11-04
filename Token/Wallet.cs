namespace BTokenCore;

internal class Wallet
{
  internal string KeyPrivateDecimal;
  internal byte[] KeyPublic;
  internal byte[] Hash160PKeyPublic;
  internal string AddressAccount;


  internal Wallet(string privKeyDec)
  {
    KeyPrivateDecimal = privKeyDec;

    KeyPublic = Crypto.GetKeyPublic(KeyPrivateDecimal);

    Hash160PKeyPublic = Crypto.ComputeHash160(KeyPublic);

    AddressAccount = Hash160PKeyPublic.BinaryToBase58Check();
  }

  internal byte[] GetSignature(byte[] dataToBeSigned)
  {
    return Crypto.GetSignature(KeyPrivateDecimal, dataToBeSigned);
  }
}