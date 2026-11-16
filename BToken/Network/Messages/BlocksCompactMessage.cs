namespace BTokenCore;

internal partial class NetworkBToken : Network
{
  class BlocksCompactMessage : NetworkMessage
  {
    internal const string Command = "compacts";

    internal const int COUNT_MAX_BLOCKS_COMPACT = 2000;
    internal const int SIZE_BUFFER_PAYLOAD = 4_000_000;

    const int MAX_LEVEL_DOS = 1;
    const int AMOUNT_DRAIN_DOS_PER_10_MINUTES = 1;


    internal BlocksCompactMessage()
      : base(
          [],
          MAX_LEVEL_DOS,
          AMOUNT_DRAIN_DOS_PER_10_MINUTES)
    { }

    internal override byte[] GetPayloadBuffer()
    {
      Payload = new byte[SIZE_BUFFER_PAYLOAD];

      return Payload;
    }

    internal override async Task Run(Peer peer)
    {
      Inventory inventoryRequested = ((PeerBToken)peer).InventoryRequested;

      if (peer.StateCurrent != Peer.StateProtocol.BlocksCompactDownload || inventoryRequested.Type != Inventory.InventoryType.MSG_BLOCKS_COMPACT)
        throw new ProtocolException($"Received unrequested {Command} message.");

      BlockchainBToken blockchainBToken = ((PeerBToken)peer).BlockchainBToken;

      int index = 0;
      int countBlocksCompact = VarInt.GetInt(Payload, ref index);

      if (countBlocksCompact > COUNT_MAX_BLOCKS_COMPACT)
        throw new ProtocolException($"Too many compact blocks {countBlocksCompact} in {Command} message.");

      List<(Header header, List<TXOutputTokenAnchor> anchors)> blocksCompact = new();

      for (int i = 0; i < countBlocksCompact; i++)
      {
        Header header = blockchainBToken.Parser.ParseHeader(Payload, ref index);
        blocksCompact.Add((header, Blockchain.ParserToken.ParseAnchors(Payload, ref index)));
      }

      if (index != LengthDataPayload)
        throw new ProtocolException($"{Command} message has {LengthDataPayload - index} bytes beyond its compact blocks.");

      Payload = null;

      await blockchainBToken.RunLocked(() => blockchainBToken.InsertBlocksCompact(inventoryRequested.Hash, blocksCompact));

      BlockMessageBToken blockMessage = (BlockMessageBToken)peer.MessagesByCommand[BlockMessage.Command];

      blockMessage.RefundInventoryTokenReceived(inventoryRequested);

      DOSMonitor.Decrement(1);

      await blockMessage.RequestBlockNext(peer);
    }
  }
}
