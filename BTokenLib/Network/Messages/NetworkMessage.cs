﻿using System;

namespace BTokenLib
{
  partial class Network
  {
    public class NetworkMessage
    {
      public string Command;
      public byte[] Payload;



      public NetworkMessage(string command)
        : this(command, new byte[0])
      { }

      public NetworkMessage(
        string command,
        byte[] payload)
      {
        Command = command;
        Payload = payload;
      }
    }
  }
}
