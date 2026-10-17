"""Chat integration checks against a fresh local server; uses existing WebSocket clients."""
import asyncio
import sys
import uuid
sys.dont_write_bytecode = True
import multiplayer_smoke as smoke

smoke.URL = sys.argv[1] if len(sys.argv) > 1 else smoke.URL
checks = 0


def check(condition, message):
    global checks
    checks += 1
    if not condition:
        raise AssertionError(message)


def chats(client):
    return [packet for packet in client.messages if packet['type'] == 'chat']


async def main():
    clients = []
    try:
        for name in ['ChatAlice', 'ChatBob', 'ChatOther', 'ChatLobby']:
            clients.append(await smoke.Client(name).connect())
        alice, bob, other, lobby = clients
        check((await lobby.send('chat', message='Not seated'))['type'] == 'error', 'Lobby cannot send table chat')
        for client, table in [(alice, 'micro'), (bob, 'micro'), (other, 'deep')]:
            check((await client.send('join', tableId=table, entryMode='wait'))['type'] == 'ack', 'Join test table')
        await smoke.wait_for(lambda: alice.table and alice.table['hand'] == 1 and bob.table['hand'] == 1)
        revision, actor, deadline = (alice.table[key] for key in ['revision', 'actor', 'deadlineUnixMs'])
        for value in ['', '   ', 'x' * 201, '\U0001f600' * 201, 'line\nbreak', 'tab\ttext']:
            result = await alice.send('chat', message=value)
            check(result['type'] == 'error', 'Invalid or oversized message rejected')
        check(not chats(alice) and not chats(bob), 'Rejected messages are not broadcast')
        check((await alice.send('chat', tableId='deep', message='Wrong table'))['type'] == 'error', 'Cross-table sending rejected')
        message_id = uuid.uuid4().hex
        check((await alice.send('chat', id=message_id, message='  Hello table!  ', username='ForgedName', revision=-999, hand=-1))['type'] == 'ack', 'Chat ignores obsolete game revision')
        await smoke.wait_for(lambda: len(chats(alice)) == 1 and len(chats(bob)) == 1)
        entry = chats(bob)[0]['chat']
        check(entry['username'] == 'ChatAlice' and entry['message'] == 'Hello table!', 'Server owns sender identity and trims padding')
        check(chats(bob)[0]['tableId'] == 'micro' and entry['sentUnixMs'] > 0, 'Chat includes table and server timestamp')
        check((await alice.send('chat', id=message_id, message='Duplicate'))['type'] == 'ack', 'Duplicate request acknowledged')
        check(len(chats(bob)) == 1, 'Duplicate command does not repeat a message')
        for text in ['x' * 200, '\U0001f600' * 200, '<b>literal markup</b>', 'Last allowed message']:
            check((await alice.send('chat', message=text))['type'] == 'ack', 'Valid message accepted')
        await smoke.wait_for(lambda: len(chats(bob)) == 5)
        check(chats(bob)[1]['chat']['message'] == 'x' * 200, '200 ASCII characters preserved')
        check(chats(bob)[2]['chat']['message'] == '\U0001f600' * 200, '200 emoji preserved')
        check(chats(bob)[3]['chat']['message'] == '<b>literal markup</b>', 'Markup stays literal chat text')
        check((await alice.send('chat', message='Too fast'))['type'] == 'error', 'Chat burst limit enforced')
        check(alice.table['revision'] == revision and alice.table['actor'] == actor and alice.table['deadlineUnixMs'] == deadline,
              'Chat does not alter betting state or deadline')
        check(not chats(other) and not chats(lobby), 'Other tables and lobby receive no messages')

        old_token, wallet_id = bob.token, bob.wallet_id
        await bob.close()
        bob = await smoke.Client('ChatBob', old_token, wallet_id).connect()
        clients[1] = bob
        await smoke.wait_for(lambda: any(packet['type'] == 'chatHistory' for packet in bob.messages))
        history = next(packet for packet in bob.messages if packet['type'] == 'chatHistory')
        check(history['tableId'] == 'micro' and len(history['chatMessages']) == 5, 'Reconnect restores table chat')
        check([entry['sequence'] for entry in history['chatMessages']] == sorted(entry['sequence'] for entry in history['chatMessages']), 'History preserves message order')
        check((await bob.send('chat', message='Back at the table'))['type'] == 'ack', 'Reconnected player can chat')

        # Six seated identities exercise bounded history independently of the turn order.
        for number in range(4):
            writer = await smoke.Client('Writer' + str(number)).connect()
            clients.append(writer)
            await writer.send('join', tableId='micro', entryMode='wait')
            for index in range(5):
                check((await writer.send('chat', message=f'Batch {number}/{index}'))['type'] == 'ack', 'Retention message accepted')
        for index in range(4):
            check((await bob.send('chat', message=f'Bob {index}'))['type'] == 'ack', 'Reconnect preserves sender rate-limit accounting')
        await asyncio.sleep(10.1)
        check((await alice.send('chat', message='Newest retained message'))['type'] == 'ack', 'Chat rate limit recovers')
        await bob.close()
        bob = await smoke.Client('ChatBob', old_token, wallet_id).connect()
        clients[1] = bob
        await smoke.wait_for(lambda: any(packet['type'] == 'chatHistory' for packet in bob.messages))
        history = next(packet['chatMessages'] for packet in bob.messages if packet['type'] == 'chatHistory')
        check(len(history) == 30 and history[0]['sequence'] == 2 and history[-1]['message'] == 'Newest retained message', 'History retains only the newest 30 messages')
        await other.send('chat', message='Previous group')
        await other.send('leave')
        await smoke.wait_for(lambda: other.table is None)
        await lobby.send('join', tableId='deep', entryMode='wait')
        await smoke.wait_for(lambda: any(packet['type'] == 'chatHistory' for packet in lobby.messages))
        check(next(packet['chatMessages'] for packet in lobby.messages if packet['type'] == 'chatHistory') == [], 'Empty table clears previous-group chat')
        check(all(client.fault is None for client in clients), 'No wire privacy failures')
        print(f'PASS: {checks} chat assertions; table isolation, sender identity, Unicode limits, duplicate requests, reconnect history and spam control.')
    finally:
        for client in clients:
            try:
                await client.close()
            except Exception:
                pass


if __name__ == '__main__':
    asyncio.run(main())
