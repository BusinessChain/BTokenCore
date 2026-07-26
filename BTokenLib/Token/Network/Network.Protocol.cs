//using System;
//using System.Collections.Generic;
//using System.Security.Cryptography;


//namespace BTokenLib
//{
//  public partial class Network
//  {
//    void ReceivedCommand(string command, Peer peer)
//    {
//      if (command == "tx")
//      {
//        TX tX = Token.ParseTX(tXRaw, SHA256);

//        Token.InsertTXUnconfirmed(tX);
//      }
//      else if (command == "getheaders")
//      {
        
//      }
//      else if (command == "notfound")
//      {
//        NotFoundMessage notFoundMessage = new(Payload);

//        notFoundMessage.Inventories.ForEach(
//          i => $"Did not find {i.Hash.ToHexString()}".Log(this, LogFile, Token.LogEntryNotifier));
//      }
//      else if (command == "inv")
//      {
//        InvMessage invMessage = new(Payload);

//        List<Inventory> inventoriesRequest = invMessage.Inventories.Where(
//          i => i.IsTX() && !Token.TryGetTX(i.Hash, out TX tXInPool)).ToList();

//        if (inventoriesRequest.Count > 0)
//          SendMessage(new GetDataMessage(inventoriesRequest));
//      }
//      else if (command == "getdata")
//      {
//        GetDataMessage getDataMessage = new(Payload);

//        foreach (Inventory inventory in getDataMessage.Inventories)
//          if (inventory.Type == InventoryType.MSG_TX)
//          {
//            if (Token.TryGetTX(inventory.Hash, out TX tXInPool))
//              await SendMessage(new TXMessage(tXInPool.TXRaw));
//            else
//              await SendMessage(new NotFoundMessage(
//                new List<Inventory>() { inventory }));
//          }
//          else if (inventory.Type == InventoryType.MSG_BLOCK)
//          {
//            if (Network.TryLoadBlock(inventory.Hash, out Block block))
//            {
//              $"Send block {inventory}.".Log(this, LogFile, Token.LogEntryNotifier);
//              await SendMessage(new MessageBlock(block.Buffer, block.LengthDataPayload));
//            }
//            else
//              await SendMessage(new NotFoundMessage(
//                new List<Inventory>() { inventory }));
//          }
//          else if (inventory.Type == InventoryType.MSG_DB)
//          {
//            if (Token.TryGetDB(inventory.Hash, out byte[] dataDB))
//              await SendMessage(new MessageDB(dataDB));
//          }
//          else
//            await SendMessage(new RejectMessage(inventory.Hash));
//      }
//      else if (command == "dataDB")
//      {
//        if (IsStateDBDownload())
//        {
//          byte[] hashDataDB = SHA256.ComputeHash(
//            Payload,
//            0,
//            LengthDataPayload);

//          if (!hashDataDB.IsAllBytesEqual(HashDBDownload))
//            throw new ProtocolException(
//              $"Unexpected dataDB with hash {hashDataDB.ToHexString()}.\n" +
//              $"Excpected hash {HashDBDownload.ToHexString()}.");

//          ResetTimer();

//          //if (Network.InsertDB_FlagContinue(this))
//          //  await RequestDB();
//          //else
//          //  SetStateIdle();
//        }
//      }
//      else if (command == "ping")
//      {
//        $"Received ping message.".Log(this, LogFile, Token.LogEntryNotifier);

//        await SendMessage(new PongMessage(Payload));
//      }
//      else if (command == "addr")
//      {
//        AddressMessage addressMessage = new(Payload);

//        foreach (NetworkAddress address in addressMessage.NetworkAddresses)
//        {
//          string addressString = address.IPAddress.ToString();

//          if (!IPAddresses.Contains(addressString))
//            IPAddresses.Add(addressString);
//        }
//      }
//      else if (command == "sendheaders")
//      {
//        await SendMessage(new SendHeadersMessage());
//      }
//      else if (command == "feefilter")
//      {
//        FeeFilterMessage feeFilterMessage = new(Payload);
//        FeeFilterValue = feeFilterMessage.FeeFilterValue;
//      }
//      else if (command == "reject")
//      {
//        RejectMessage rejectMessage = new(Payload);

//        $"Get reject message: {rejectMessage.GetReasonReject()}".Log(this, LogFile, Token.LogEntryNotifier);
//      }
//    }
//  }
//}