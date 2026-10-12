# BTokenCore: software description

BTokenCore is a C# (.NET 8) node for BToken, a token blockchain anchored in Bitcoin. This file explains how the node works and where each part lives in the code. It describes the code as it is and holds no plan decisions.

## Overview

A node runs two blockchains side by side: Bitcoin and BToken. Each has its own network of peers. A miner builds BToken blocks and anchors them in Bitcoin.

Bitcoin leads and BToken follows. Bitcoin decides which BToken blocks exist: a BToken block counts only if Bitcoin carries its anchor. When Bitcoin inserts or rolls back a block, BToken reacts.

Both blockchains share one lock. Bitcoin raises its events while it holds that lock, so BToken handles them under the same lock without taking it again. The one exception is startup: when Bitcoin loads its stored chain, before the networks start, it raises its insert event without the lock.

## Anchoring

A BToken miner publishes a BToken block's hash and its previous hash in a Bitcoin transaction output, the anchor. In each Bitcoin block, the first anchor per token wins. It wins unconditionally, whatever block it builds on. So BToken's headers can branch even when Bitcoin's chain doesn't, and BToken needs its own fork choice.

BToken headers do not come from peers. Each winning anchor creates a placeholder header, linked to the Bitcoin header that created it. When Bitcoin inserts a block, BToken inserts the placeholder into its header tree, loads its stored block at the placeholder's height if there is one, parses it against the placeholder and applies the queued blocks. When Bitcoin rolls the block back, BToken removes the placeholder and applies the root chain's queued blocks. Then, as long as the strongest connected fork reaches higher than the root chain's applied tip, BToken reorgs to it and applies the root chain's queued blocks again. BToken peers only announce block hashes and deliver block data.

## Header tree

Each blockchain keeps its headers in a tree of chains.

The root chain is the main chain. Its applied tip is the highest block applied to the token's state, for example to BToken's accounts.

A fork is a child chain. It branches off a header of its parent chain. Its tip of downloaded blocks is the highest block it holds without a gap, counted from its own first header.

A fork is connected if it has a downloaded block and the header it branches off from lies at or below its parent's tip: the applied tip for the root chain, the tip of downloaded blocks for a fork. Only a connected fork's blocks reach the root chain's applied tip without a gap.

Each chain holds its queued blocks, which are downloaded but not yet applied, and its headers awaiting a block, whose download was requested.

## Life of a block

1. A dispatcher picks the next missing block and requests it from an idle peer. It only picks blocks the peer can deliver: a block whose Bitcoin parent is the newest Bitcoin header is requested only from a peer that announced it, and a peer that answered notfound for a block is not asked for it again. Once every block within a chain's download window has been requested, an idle peer gets the lowest block still awaiting delivery in that chain, even if another peer is downloading it. This redundancy is deliberate: a slow peer must not stall the sync.
   Where the search looks depends on the peer. For a peer that has delivered headers, which in practice means a Bitcoin peer, only the chain holding that peer's last header is searched, and only if that header lies above the root chain's applied tip; the search first walks down to any parent chain whose blocks lag behind the start of its fork. For a peer without delivered headers, which in practice means a BToken peer, the whole tree is searched: the main chain first, the forks only when the main chain has nothing to hand out, so a redundant request for a main-chain block takes precedence over the first request for a fork's block. This never stalls a fork for good, because a peer that does not deliver a requested block within the timeout is disconnected.
2. The peer delivers the block. The chain awaiting it queues the block and parses it against the awaited header.
3. If the block lands on a fork, the fork advances its tip of downloaded blocks. If the fork is then stronger than the root chain, the node reorgs to it.
4. The root chain applies its queued blocks above its applied tip, stores them and announces each inserted block.

A reorg walks from the fork up to the root: each chain on the path switches places with its parent. Before the root chain's direct child on that path switches, the root chain rolls back to the header that child branches off from. Each rolled-back block is undone in the token's state, its rollback is announced, and the block is queued again. Bitcoin also deletes the block's stored header; BToken stores no headers. Stored blocks are kept.

Removing a header also removes everything above it in its chain and every fork branching at or above it. If the removed header was the chain's first header, the chain leaves its parent. Otherwise the chain takes over its strongest fork branching at the new tip.

## Token state

Each blockchain has a token that parses headers and transactions and applies and undoes blocks.

The Bitcoin token tracks only the wallet's spendable outputs. It does not validate Bitcoin's state.

The BToken token uses accounts. An account has an ID, a balance, a nonce and a creation height. Applying or undoing a block first stages the affected accounts, then writes them; an account without balance is deleted. A transaction pool holds waiting transactions, grouped by sender and ordered by fee.

A protocol exception signals invalid data from a peer or in a block.

## Network

Each blockchain has a network that manages its peers. Each peer runs a protocol state machine with one handler per message type: version, verack, headers, getdata, block, notfound, ping, and one handler for unknown messages. Bitcoin peers also handle getheaders and inv. A denial-of-service monitor limits misbehaving peers.

## Miner

The miner can build a BToken block on the root chain's applied tip and anchor it in a Bitcoin transaction, but nothing switches mining on yet, so it never mines. When Bitcoin inserts a block, the miner only clears its self-mined blocks whose anchor won. Inserting self-mined blocks is not implemented yet.

## Where to find it

| Concept | Code name | File |
|---|---|---|
| Node, wiring of the two blockchains | `Node` | `Node.cs` |
| Blockchain, block flow, reorg, rollback | `Blockchain` | `Blockchain/Blockchain.cs` |
| Chain in the header tree | `Chain` | `Blockchain/Chain.cs` |
| Root chain | `ChainRoot` | `Blockchain/Blockchain.cs` |
| Fork, parent chain | `ChainsChild`, `ChainParent` | `Blockchain/Chain.cs` |
| Applied tip, tip of downloaded blocks | `HeaderTipBlockchain` | `Blockchain/Chain.cs` |
| Chain's first and last header | `HeaderRoot`, `HeaderTip` | `Blockchain/Chain.cs` |
| Queued blocks | `Blocks` | `Blockchain/Chain.cs` |
| Headers awaiting a block | `HeadersAwaitingBlock` | `Blockchain/Chain.cs` |
| Block pool (recycled blocks) | `PoolBlocks` | `Blockchain/Blockchain.cs` |
| Placeholder insertion and removal | `InsertHeaderPlaceholder`, `RemoveHeaderPlaceholder` | `Blockchain/Blockchain.cs` |
| Next missing block | `FetchHeaderBlockMissingNext` | `Blockchain/Blockchain.cs` |
| Queueing a block | `TryInsertBlock`, `TryQueueBlock` | `Blockchain/Blockchain.cs`, `Blockchain/Chain.cs` |
| Applying queued blocks | `InsertBlocksQueued` | `Blockchain/Blockchain.cs` |
| Fork strength, reorg, switch | `IsStrongerThan`, `Reorg`, `SwitchWithParent` | `Blockchain/` |
| Strongest connected fork | `FindForkStrongest` | `Blockchain/Chain.cs` |
| Rollback | `RollBack` | `Blockchain/Blockchain.cs` |
| Cutting off a chain at a header | `CutOff` | `Blockchain/Chain.cs` |
| Insert and rollback announcements | `OnBlockInserted`, `OnBlockRolledBack` | `Blockchain/Blockchain.cs` |
| Header, header links, creating Bitcoin header | `Header`, `HeaderPrevious`, `HeaderNext`, `HeaderParent` | `Token/Header.cs` |
| Winning anchors of a Bitcoin block | `AnchorsWinner` | `Token/Header.cs`, `Token/Block.cs` |
| Anchor | `TXOutputTokenAnchor` | `Token/TXOutputTokenAnchor.cs` |
| Block parsing and verification | `Block` | `Token/Block.cs` |
| Token base | `Token` | `Token/Token.cs` |
| Bitcoin token | `TokenBitcoin` | `Bitcoin/TokenBitcoin.cs` |
| BToken token, placeholder creation | `TokenBToken`, `CreateHeaderPlaceholder` | `BToken/TokenBToken.cs` |
| Account | `Account` | `BToken/Account.cs` |
| Transaction pool | `PoolTXBToken` | `BToken/PoolTXBToken.cs` |
| Protocol exception | `ProtocolException` | `ProtocolException.cs` |
| Network, peer, messages | `Network`, `Peer`, `NetworkMessage` | `Network/` |
| Denial-of-service monitor | `DOSMonitorPer10Minutes` | `Network/DOSMonitorPer10Minutes.cs` |
| Miner | `Miner` | `Miner.cs` |

The sibling repository `BTokenCore-Testbench` holds the tests and the plans. It is not part of the node.
