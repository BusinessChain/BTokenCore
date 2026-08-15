using System;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;


namespace BTokenCore;

internal class Wallet
{
  internal SHA256 SHA256 = SHA256.Create();

  internal string KeyPrivateDecimal;
  internal byte[] KeyPublic;
  internal byte[] Hash160PKeyPublic = new byte[20];
  internal string AddressAccount;


  internal Wallet(string privKeyDec)
  {
    KeyPrivateDecimal = privKeyDec;

    KeyPublic = Crypto.GetPubKeyFromPrivKey(KeyPrivateDecimal);

    Hash160PKeyPublic = Crypto.ComputeHash160(KeyPublic, SHA256);

    AddressAccount = Hash160PKeyPublic.BinaryToBase58Check();
  }

  internal byte[] GetSignature(byte[] dataToBeSigned)
  {
    return Crypto.GetSignature(KeyPrivateDecimal, dataToBeSigned);
  }
}