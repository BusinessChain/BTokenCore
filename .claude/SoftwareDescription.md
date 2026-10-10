# BTokenCore: software description

BTokenCore is a C# (.NET 8) node for BToken, a token blockchain anchored in Bitcoin. This file explains how the node works and where each part lives in the code. It describes the code as it is and holds no plan decisions.

## Overview

A node runs two blockchains side by side: Bitcoin and BToken. Each has its own network of peers. A miner builds BToken blocks and anchors them in Bitcoin.

Bitcoin leads and BToken follows. Bitcoin decides which BToken blocks exist: a BToken block counts only if Bitcoin carries its anchor. When Bitcoin inserts or rolls back a block, BToken reacts.

Both blockchains share one lock. Bitcoin raises its events while it holds that lock, so BToken handles them under the same lock without taking it again.

## Anchoring

A BToken miner publishes a BToken block's hash and its previous hash in a Bitcoin transaction output, the anchor. In each Bitcoin block, the first anchor per token wins. It wins unconditionally, whatever block it builds on. So BToken's headers can branch even when Bitcoin's chain doesn't, and BToken needs its own fork choice.

BToken headers do not come from peers. Each winning anchor creates a placeholder header, linked to the Bitcoin header that created it. When Bitcoin inserts a block, BToken inserts the placeholder into its header tree. When Bitcoin rolls the block back, BToken removes the placeholder. BToken peers only announce block hashes and deliver block data.

## Header tree

Each blockchain keeps its headers in a tree of chains.

The root chain is the main chain. Its applied tip is the highest block applied to the token's state, for example to BToken's accounts.

A fork is a child chain. It branches off a header of its parent chain. Its tip of downloaded blocks is the highest block it holds without a gap, counted from its own first header.

Each chain holds its queued blocks, which are downloaded but not yet applied, and its headers awaiting a block, whose download was requested.

## Life of a block

1. A dispatcher picks the next missing block of the tree and requests it from an idle peer.
2. The peer delivers the block. The chain awaiting it queues the block and parses it against the awaited header.
3. If the block lands on a fork, the fork advances its tip of downloaded blocks. If the fork is then stronger than the root chain, the node reorgs to it.
4. The root chain applies its queued blocks above its applied tip, stores them and announces each inserted block.

A reorg rolls the root chain back to the fork's branching point. Each rolled-back block is undone in the token's state, its stored header is deleted, its rollback is announced, and the block is queued again. Then the fork switches places with its parent, chain by chain, up to the root.

Removing a header also removes everything above it in its chain. The chain then takes over its strongest fork branching at the new tip.

## Token state

Each blockchain has a token that parses headers and transactions and applies and undoes blocks.

The Bitcoin token tracks only the wallet's spendable outputs. It does not validate Bitcoin's state.

The BToken token uses accounts. An account has an ID, a balance, a nonce and a creation height. Applying or undoing a block first stages the affected accounts, then writes them; an account without balance is deleted. A transaction pool holds waiting transactions, grouped by sender and ordered by fee.

A protocol exception signals invalid data from a peer or in a block.

## Network

Each blockchain has a network that manages its peers. Each peer runs a protocol state machine with one handler per message type: version, headers, getheaders, getdata, block, inv, tx and ping. A denial-of-service monitor limits misbehaving peers.

## Miner

The miner builds a BToken block on the root chain's applied tip and anchors it in a Bitcoin transaction. Inserting self-mined blocks is not implemented yet.

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
| Rollback | `RollBack` | `Blockchain/Blockchain.cs` |
| Header removal | `RemoveHeaders` | `Blockchain/Chain.cs` |
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
