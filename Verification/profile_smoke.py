"""Guest onboarding/profile checks against a fresh local server."""
import asyncio
import json
import sys
import uuid
sys.dont_write_bytecode = True
import websockets
import multiplayer_smoke as smoke

smoke.URL = sys.argv[1] if len(sys.argv) > 1 else smoke.URL
checks = 0


def check(condition, message):
    global checks
    checks += 1
    if not condition:
        raise AssertionError(message)


async def main():
    clients = []
    try:
        alice = await smoke.Client('ProfileAlice').connect()
        bob = await smoke.Client('ProfileBob').connect()
        clients.extend([alice, bob])
        welcome = next(packet for packet in alice.messages if packet['type'] == 'welcome')
        check(welcome['protocol'] == smoke.PROTOCOL and welcome['username'] == 'ProfileAlice' and welcome['avatar'] == 2,
              'No-invite onboarding confirms the canonical profile')
        check(len(alice.rows) == 3 and alice.wallet_balance == 10000, 'First connection reaches the lobby')
        token, wallet = alice.token, alice.wallet_id
        check((await alice.send('profile', username='  Alice Lobby  ', avatar=5))['type'] == 'ack', 'Profile can change in the lobby')
        response = next(packet for packet in alice.messages if packet['type'] == 'profile')
        check(response['username'] == 'Alice Lobby' and response['avatar'] == 5, 'Server confirms normalized profile')
        check(alice.token == token and alice.wallet_id == wallet and alice.wallet_balance == 10000, 'Lobby rename keeps identity and bank')
        for name in ['', 'A', 'x' * 21, '   ', '<invalid>', 'line\nbreak']:
            check((await alice.send('profile', username=name, avatar=5))['type'] == 'error', 'Invalid display names rejected')
        check((await alice.send('profile', username='profilebob', avatar=5))['type'] == 'error', 'Duplicate name rejected case-insensitively')
        check((await alice.send('profile', username='Alice Lobby', avatar=8))['type'] == 'error', 'Invalid avatar rejected')
        check((await alice.send('join', tableId='micro', entryMode='wait'))['type'] == 'ack', 'First player seated')
        check((await bob.send('join', tableId='micro', entryMode='wait'))['type'] == 'ack', 'Second player seated')
        await smoke.wait_for(lambda: alice.table and alice.table['hand'] == 1 and bob.table and bob.table['hand'] == 1)
        before = alice.table
        seat = before['yourSeat']
        finances = [(p['stack'], p['bet']) for p in before['players']]
        revision, actor, deadline = (before[key] for key in ['revision', 'actor', 'deadlineUnixMs'])
        command_id = uuid.uuid4().hex
        check((await alice.send('profile', id=command_id, username='Alice Table', avatar=7, revision=-99, hand=-99))['type'] == 'ack', 'Name change works during a hand without a game revision')
        await smoke.wait_for(lambda: bob.table['players'][seat]['username'] == 'Alice Table')
        check(alice.table['players'][seat]['avatar'] == 7 and bob.table['players'][seat]['avatar'] == 7, 'Profile changes reach all table players')
        check([(p['stack'], p['bet']) for p in alice.table['players']] == finances, 'Name change does not alter chips')
        check(alice.table['revision'] == revision and alice.table['actor'] == actor and alice.table['deadlineUnixMs'] == deadline,
              'Profile changes preserve action revision and turn timer')
        check(alice.token == token and alice.wallet_id == wallet, 'Table rename preserves reconnect identity')
        check((await alice.send('profile', id=command_id, username='Should Not Apply', avatar=1))['type'] == 'ack', 'Duplicate update acknowledged')
        check(alice.table['players'][seat]['username'] == 'Alice Table', 'Duplicate update does not apply twice')
        check((await alice.send('profile', username='ProfileBob', avatar=0))['type'] == 'error', 'Conflicting update rejected in a hand')
        check(alice.table['players'][seat]['username'] == 'Alice Table' and alice.table['players'][seat]['avatar'] == 7, 'Failed update is atomic')
        check((await alice.send('chat', message='My new name'))['type'] == 'ack', 'Renamed player can chat')
        await smoke.wait_for(lambda: any(packet['type'] == 'chat' for packet in bob.messages))
        check(next(packet for packet in bob.messages if packet['type'] == 'chat')['chat']['username'] == 'Alice Table', 'Chat uses the new display name')
        await alice.close()
        alice = await smoke.Client('Alice Table', token, wallet).connect()
        clients[0] = alice
        restored = next(packet for packet in alice.messages if packet['type'] == 'welcome')
        check(restored['username'] == 'Alice Table' and restored['avatar'] == 7, 'Reconnect confirms current name and avatar')
        await smoke.wait_for(lambda: alice.table is not None)
        check(alice.table['yourSeat'] == seat and alice.wallet_balance == 9800, 'Reconnect keeps the same seat and bank')
        released = await smoke.Client('Alice Lobby').connect()
        clients.append(released)
        check(len(released.rows) == 3, 'Previous display name becomes available')
        async with websockets.connect(smoke.URL) as peer:
            await peer.send(json.dumps(dict(type='hello', username='Wrong Wallet', token=token, walletId=uuid.uuid4().hex, protocol=smoke.PROTOCOL)))
            error = json.loads(await peer.recv())
            check(error['type'] == 'error' and 'different wallet' in error['message'], 'Reconnect token still authenticates its wallet')
        check(all(client.fault is None for client in clients), 'No card privacy failures')
        print(f'PASS: {checks} profile assertions; no invite, validation, live rename/avatar, chip preservation, duplicate protection and reconnect identity.')
    finally:
        for client in clients:
            try:
                await client.close()
            except Exception:
                pass


if __name__ == '__main__':
    asyncio.run(main())
