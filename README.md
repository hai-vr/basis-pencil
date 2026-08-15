basis-pencil
=====

### Networking

**Late joiners**: We will retransmit the drawn lines to late joiners. The network owner of the prop
will submit each line drawn as a single packet with multiple recipients. The recipients are those who have
loaded the prop but not received that line yet, so we're trying to limit repetition of packets when multiple
players join the server within a short timespan.
- Note: There seems to be reliability issues in the current implementation, it is not clear what causes it.

**Drawing live**: When someone is drawing, the points of the line are transmitted immediately without
requiring a round trip to the network owner. It does not attempt to fake using networked IK, so sometimes
the lines will be drawn a few dozen of milliseconds before the arm of the avatar moves.

Lines with more than 200 points are intentionally split while they are drawn to avoid emulation timeouts and other hitches.

Note: The code for drawing live is currently poorly optimized, as it creates a new mesh each time a new point
  is drawn. This could be drastically improved by being able to update the state of multiple lines being drawn.

**Ownership transfer**: Ownership transfer is not handled properly and could use a lot of work.

If the network owner leaves in the middle of transmitting data for late joiners, those late-joiners may sometimes never
receive the completed data, or those late joiners may themselves become the owner of the prop while someone else would
have been a more adequate owner.

**Optimization of packet size**: Currently, each line is submitted as a separate packet, and we may send
multiple lines within a single frame, depending on the networking budget (around 20 lines and 200 points per line).

We submit the world-space position, world-space rotation, and world-space scale of each point in the line (as stored post-calculation,
so it may include interpolated lines).

Due to limitations related to the permissions of Cilbox at the time of writing, some of the types are not compressed
and the size of the packets in general could be greatly reduced, for example, by omitting the scale if all points were to have the same scale,
and by encoding the world-space positions as compressed deltas
