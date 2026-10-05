# Bugs

Verified open bugs / TODOs in BTokenCore (networking, block download, blockchain, mining), kept so we can fix them later one by one.

Bug/TODO list requested by the user 2026-09-26. Re-verified against the code 2026-10-01 (removed as fixed/obsolete: #5 lock mismatch — one shared semaphore now; #14 inv limit 200 gone; #20 TryGetBlockMined NRE — now in `Miner`, DB by hash; #26 IsMining on wrong network — `Miner` owns the flag). Re-check before fixing (user edits in parallel). Remove items once fixed. Every fix needs approval per CLAUDE.md.

Class names as of 2026-10-01: `Blockchain` (chain + persistence, `Blockchain\Blockchain.cs`), `Branch` (nested tree node, `Blockchain\Branch.cs`), `Network` (peers + dispatcher), `Miner` (mining, `Miner.cs`).

**Blockers (download never works end-to-end)**
3. FIXED 2026-10-01 per user's design: `Branch(parent, headerRoot, bool isRoot)` sets `HeaderTipBlockchain = headerRoot` only for the root; non-root branches start with null and branch-reading consumers must handle null (root-only readers need not). `LoadBlockchain` advances root tip. Null handling added in `TryGetBlockNext` (null → next = HeaderRoot) (`Promote` since removed). Leftover: root's `HeaderDownloadNext = genesis` requests the genesis block, which then sits in `QueueBlocks` forever; after a load `HeaderDownloadNext` still points at genesis → loaded blocks re-downloaded, duplicate DB `_id` (tie into #2).
2. `Branch.HeaderDownloadNext` becomes null once the cursor passes the tip (`FetchHeaderDownload`), `AppendHeader` never revives it → new headers at the tip are never downloaded.

**Re-verified 2026-10-04 (Branch → `Chain`, `Blockchain\Chain.cs`; download rule: child chain gets downloads only once all ancestors have all blocks up to its fork — implemented in `FetchHeaderDownloadAlongPath`). Still open: #2, #3 leftover, #6, #7, #9, #11, #21, #34, #35g. New:**
36. `TrySearchHeaderAncestor` skips known headers only via `HeaderNext` of the same chain → headers that continue into an existing child chain (e.g. reply to a root locator from a peer on a fork) create a duplicate sibling chain.
37. Peer with unknown tip (`HeaderTipReceivedLast == null`: inbound before headers, or after non-connecting headers) is assumed to have `ChainRoot.HeaderTip` → asked for blocks it may not have → timeout → disconnect.
38. Reorg breaks the invariant for the old root's other children forked above the reorg fork (their parent tip drops below their fork).

**FIXED 2026-10-04: derived download cursor** — `HeaderDownloadNext` removed; `Chain.FetchHeaderDownload` walks from `HeaderTipBlockchain.HeaderNext` (or `HeaderRoot` if `heightBlockNext == HeaderRoot.Height`) within the 20-block window, skipping headers in `Blocks`/`HeadersAwaitingBlock`; when nothing is free it falls back to the lowest awaiting header (that recovers abandoned requests; no timeout/release hook in Chain, by user's decision; the 60 s timeout stays only in the Network dispatcher). Fixes #2, #3 leftover, #41, and the re-download part of #34. #6 reduced (duplicates only via the fallback). #21 WITHDRAWN 2026-10-04 (user): disconnecting the delivering peer is the handling; root stays at h-1 (correct tip), next honest block forks at h-1 and wins. Leftover: honest peers get asked for h (they serve header h via #40, null tip via #37) and we never send notfound → 60 s timeout disconnects them; fixing #40 resolves it.

**Re-verified 2026-10-04 (later; `InsertBlockReturnNextDownload`, `Chain.Blocks` by height, `Reorg`). Still open: #2, #3 leftover, #6, #7, #9, #11, #21, #32, #34, #35g, #36, #37, #38. New:**
39. `Blockchain.GetBlock`: unknown hash → `GetHeader` returns null → NRE on `header.Height` → requesting peer is disconnected (should just be ignored).
40. `GetHeadersSerialized` serves headers up to `HeaderTip` (beyond `HeaderTipBlockchain`) → peers request blocks we don't have → no reply → they time out and drop us.
41. #3 leftover worsened: genesis sits in root `HeadersAwaitingBlock` (no DB block 0 → BToken peers never answer). At the tip, the awaiting fallback (MinBy) hands genesis to every peer → 60 s timeout → disconnect loop.
42. `HeaderBToken.AppendToHeader` throws if the anchoring Bitcoin block hasn't been downloaded yet (`AnchorsWinner` only filled in `Block.Parse`) → BToken peers get disconnected during initial sync until Bitcoin catches up; the two syncs aren't coordinated.
43. WITHDRAWN 2026-10-05 (user): blocks are requested as `MSG_BLOCK` (not `MSG_WITNESS_BLOCK`) → peers send witness-stripped blocks, segwit marker never arrives. BToken only recognizes non-segwit P2PKH and OP_RETURN (anchor) outputs; Bitcoin TXs are not validated (valid header incl. PoW + merkle ⇒ TXs trusted).

**FIXED 2026-10-04 (builds, untested): reorg by recursive promotion** — `Chain.SwitchWithParent()` swaps contents with the parent object (`ChainRoot` reference stays fixed): parent keeps its part up to the fork + the child's headers/`Blocks`/awaiting/children; the child object takes the parent's part above the fork; fork header's `HeaderNext` relinked; non-root tips recomputed from scratch, root's tip (= applied) untouched. `Blockchain.Reorg` promotes the winner level by level up to and including root, then rolls the state back using the demoted old-root segment (`chain.HeaderRoot` = fork + 1; rolled-back blocks go into its `Blocks`), then recomputes its tip. `SwitchWithRootChain` removed. Fixes #9, #34, #35g, #38.
**REVISED 2026-10-05 by user (reviewed by hand, untested):** `Reorg` is a single loop `while (chain != ChainRoot) { SwitchWithParent; chain = ChainParent }`; at the level whose parent is root it first calls `RollBack(heightAfterRollBack: chain.HeaderRoot.Height - 1)` (fork height), before that switch. `RollBack` puts the rolled-back blocks into `ChainRoot.Blocks` (they are unapplied now); the following `SwitchWithParent` moves everything above the fork into the demoted chain and recomputes its tip. No extra tip advance after the loop (it would have moved root's tip over unapplied blocks); the root advances only via the insert loop in `InsertBlockReturnNextDownload`. Order matters: rolling back after the switch would put old-branch blocks into the new root's `Blocks` (duplicate keys).

**Open reorg points (found 2026-10-05):**
44. Invalid block on the winning branch after a reorg: the old branch is already rolled back, `Token.InsertBlock` throws at h → root stays at h-1 (possibly = fork) while the valid demoted branch is longer. `IsStrongerThan` only runs for the chain that just received a block, so the demoted chain isn't re-checked until a new block arrives on it. The #21 withdrawal ("h-1 is the correct tip") doesn't hold in the reorg case.
45. `TokenBToken.RollBack` is a stub: `ReverseSpendInputInDB` / `ReverseOutputInDB` are empty → account DB not rolled back. Rolled-back TXs are also not returned to `TXPool`.

**Block download / branch tree**
6. Awaiting fallback (`GetHeaderAwaitingBlockLowest`) hands the same lowest header to every idle peer → duplicate block requests each dispatcher round. (It is also what re-assigns the header of a peer that disconnects mid-download — verified 2026-09-26 that such headers are NOT orphaned, keep that property when fixing.)
7. `HeadersMessage.Run`: non-connecting headers set `HeaderTipReceivedLast = null` → peer drops out of downloading; should send getheaders with locator instead. `SendGetHeaders` there not awaited.
34. Reorg leftover: after `SwitchWithRootBranch` the old root's rolled-back blocks are deleted from DB and not kept in its `BlocksBranch`, while its `HeaderDownloadNext` already points past them → never re-downloaded if the old chain becomes stronger again. Also: `ReorgIfStronger` assumes the root's applied tip is at or above the fork; if it lags below, the branch blocks are applied on a state below the fork.
35. FIXED 2026-10-01 (a–f) by refactoring, user's outline: `Blockchain.InsertBlockReturnNextBlock` → root: `InsertBlocksRoot()`; else `branch.StageBlocks()` (queue → `BlocksBranch`, memory only, no limit) + `ReorgIfStronger(branch)` (rollback loop loading blocks from DB, apply `TakeBlocksBranch()`, `SwitchWithRootBranch`, `BranchRoot = branch` once). Shared `Blockchain.InsertBlock(block)`. `Branch.TryGetBlockNext(out block)` is a plain query; `Promote` hands over `BlocksBranch`. Root field renamed `BranchRoot` by user. Still open: (g) branch deeper than root's direct child: fork height wrong, `SwitchWithRootBranch` breaks the tree (= #7 in chat numbering).
(Earlier 2026-10-01 note, possibly obsolete: fixed #8, #33, #34b via refactoring — `FlushBlocksToDatabase` replaced; user's design: callers (`InsertBlockReturnNextBlock`, `InsertBlockMined`) dispatch `if (branch == BlockchainRoot) InsertBlocksRoot(); else ReorgIfStronger(branch);` — `InsertBlocksRoot` forward only; `ReorgIfStronger` (Branch.StageBlocks, IsStrongerThan, rollback loop loading blocks from DB, SwitchWithRootBranch incl. BlocksBranch hand-over, `BlockchainRoot = branch` once). Branch blocks live in `BlocksBranch` (memory only, no limit, user's design).)
9. `SwitchWithRootBranch` doesn't re-link the fork header's `HeaderNext`; also doesn't move queue/awaiting/branches below the fork like `Promote` does.
21. Invalid block in `InsertBlocksRoot` (formerly `FlushBlocksToDatabase`) (`Token.InsertBlock` throws, e.g. BToken spend from empty account): `TryGetBlockNext` already advanced `HeaderTipBlockchain` and removed the block from the queue → chain claims the block is applied, later queued blocks get applied on top. Header never marked invalid, branch never cut. Fix: cut the chain at that header (header + descendants invalid), reset tip, remember hash as rejected. Discussed 2026-09-27.

**Messages**
11. `HeadersMessage.SendHeaders` prefixes the byte count instead of the header count.
12. `PongMessage.Run` sets `peer.ProtocolStateMachine = null` (Pong isn't registered, so dormant, but wrong).
13. `UnknownMessage.SIZE_BUFFER_PAYLOAD = 4_000_000` probably too big per peer; ~100 KB suggested (user asked about it in a code comment).
15. Sender-side tx throttling: per-peer outgoing byte bucket (~90% of `TXMessage` limit, fee-rate ordered queue) in `Peer.BroadcastTX` / `Miner.MineTokenAnchor`, so we never break our own DoS rule.

**Network**
17. `COUNT_MAX_OUTBOUND_CONNECTIONS = 1` and hard-coded IP in `Network.GetPeer`.

**Mining** (done so far: `Miner.OnBlockBitcoinInserted` reads `AnchorsWinner`, inserts own winning block via `InsertBlockMined`, announces header; `HeaderBToken.AppendToHeader` checks the header is anchored in the parent chain and sets `HeaderParent`)
22. Anchor serialization: `TXOutputTokenAnchor.Serialize()` omits the `OP_RETURN`/length prefix that parsing expects; `LengthDataAnchorToken = 70` but payload is 71 bytes. Also `TXBitcoin.Serialize` iterates `foreach (TXOutputBitcoin output ...)` → InvalidCastException on the anchor output. → own anchors can never be created/recognized.
23. Foreign winning anchors: P2P BToken blocks are accepted via `InsertBlockReturnNextBlock`; headers are anchor-checked in `AppendToHeader`, but no re-mine when the BToken tip changes (block arrives).
24. Stale anchor TXs: only one open anchor at a time; each re-mine must replace the previous unconfirmed anchor (RBF, same inputs, higher fee), else a stale anchor can win a later slot and waste it.
25. Bitcoin reorg: on Bitcoin rollback (rollback loop in `Blockchain.ReorgIfStronger`) nothing notifies BToken; BToken blocks whose `HeaderParent` is rolled back must be rolled back too.
27. `TokenBToken.CreateTXCoinbase` never adds `tXOutput` to `tX.TXOutputs` (and sets no `Value`) → block reward lost.
28. `TokenBitcoin.TryCreateTXAnchor` doesn't mark spent inputs → next anchor double-spends. Also `OutputsSpendableConfirmed` is never filled (`InsertBlock` fills `OutputsSpendable`) → always returns false.
29. Starting the miner: `IsMining` is never set true; when switched on (future RPC), mine immediately instead of waiting for the next Bitcoin block.
30. Catch-all `catch { return; }` in `Miner.OnBlockBitcoinInserted` hides all errors. Untranslated German comments: `TokenBitcoin.cs` (IndexTXs), `TokenBToken.cs` (TryCreateTXAnchor), `HeaderBToken.cs`.

**Testbench**
31. `StartNode` test's check "each peer's first sent message is `version`" was deleted by the user 2026-09-30 after peers moved into `Network` (private `Peers`). Re-add later; options: make `Network.Peers` internal, or observe sockets via the test `ICommunication`.

**Why:** user wants a persistent list to come back to later.
**How to apply:** when the user asks for "the bug list" or picks the next bug, read this, re-verify the item, propose the fix, wait for approval. Fix order: largest impact first.
