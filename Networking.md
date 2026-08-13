Networking
=====

The networking of the pencil is essentially just syncing a non-nested KV store in no specific order,
while attempting to submit data to late joiners as efficiently as possible by making use of the recipients array.

- When a new entry in the KV store is received, instantiate the completed mesh into a new GameObject.
- If we ever delete an entry from the KV store, destroy that GameObject.

The owner takes care of adding items to the KV store:
- by listening to the termination signal of the current line being drawn, or
- by emitting the termination themselves, when the owner is the one drawing it.

----

- ⬜ The owner is the person who loaded the prop first.
- ⬜ When someone draws with the pen, the ownership of the networking responsibility does not change.
  - Therefore, the owner is not necessarily the person who is currently drawing with the pen.
- ⬜ We want to minimize ownership transfer.

## New line data transfer

- ⬜ The drawer submits data packets to everyone (not just the owner) describing the line they're drawing, as they are currently drawing it.
- ⬜ The drawer submits a data packet when a line finishes drawing.

## Initialization and late joining

- ✅ When a user loads the prop, they ask the owner to send all data.
- ✅ When the owner is asked for all data by a user, they:
  - ✅ add the playerId to a Dict<dataIndex, List<playerId>>.
  - ✅ add the playerId to a List<playerId> that remembers all playerIds that have loaded the prop.

## Players leaving

When a user leaves the server:
- ✅ We stop remembering that playerId from the players who loaded the prop.
- ✅ We scrub that playerId out of the Dict<dataIndex, List<playerId>>

## New lines are just added to the Dict storage

- ⬜ If the drawer is also the owner, the new line is directly added to the Dict.
- ⬜ If the drawer is not the owner, the received line until the termination packet is added to the Dict when it is received by the owner.

Then:

- ⬜ When lines are being added, the dataIndex to be sent are added to a List.
- ⬜ The owner adds them to the Dict<dataIndex, List<playerId>>, where List<playerId> is the list that remembers all playerIds
  that have loaded the prop.
- ⬜ These new lines should somehow be sent in priority, so a separate List<dataIndex> keeps track of which dataIndex should be sent next.

## The owner network loop

- 🟨 The owner loops over time:
    - ⬜ through all non-empty dataIndex in the Dict, prioritizing using the List<dataIndex> if it is not empty,
    - ✅ send a Reliable Non-Ordered packet of that dataIndex to those playerIds as recipients,
    - ✅ empty that dataIndex from that Dict.

## Ownership transfer

If there is an ownership transfer, catch-up may be incomplete. Figure out what to do from there.
