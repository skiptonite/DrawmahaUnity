"""Run against a local server. Requires Python 3.11+ and websockets.
Start with DRAWMAHA_ACCESS_KEY=test-invite-key-123456,
DRAWMAHA_SHOWDOWN_SECONDS=1 and DRAWMAHA_RECONNECT_SECONDS=3.
"""
import asyncio, json, sys, uuid
import websockets

URL = sys.argv[1] if len(sys.argv) > 1 else 'ws://127.0.0.1:8080/ws'
KEY = 'test-invite-key-123456'
assertions = 0

def check(value, name):
    global assertions
    assertions += 1
    if not value: raise AssertionError(name)

async def wait_for(predicate, timeout=15):
    async with asyncio.timeout(timeout):
        while not predicate(): await asyncio.sleep(.025)

class Client:
    def __init__(self, name, token=None, wallet_id=None):
        self.name, self.token = name, token
        self.wallet_id = wallet_id or uuid.uuid4().hex
        self.wallet_balance = 0
        self.table = None
        self.rows = []
        self.messages = []
        self.fault = None
    async def connect(self, key=KEY):
        self.ws = await websockets.connect(URL)
        await self.ws.send(json.dumps(dict(type='hello', username=self.name, token=self.token, walletId=self.wallet_id, avatar=2, accessKey=key)))
        self.task = asyncio.create_task(self.reader())
        await wait_for(lambda: self.token or any(m['type']=='error' for m in self.messages))
        if key == KEY: await wait_for(lambda: len(self.rows)==3)
        return self
    async def reader(self):
        try:
            async for frame in self.ws:
                m=json.loads(frame); self.messages.append(m)
                if m['type']=='welcome': self.token=m['token']; self.wallet_balance=m['walletBalance']
                if m['type']=='wallet': self.wallet_balance=m['walletBalance']
                if m['type']=='lobby': self.rows=m['tables']
                if m['type']=='left': self.table=None
                if m['type']=='state':
                    t=m['table']; self.table=t
                    check(len(t['players'])==6, 'Exactly six seat records')
                    for p in t['players']:
                        if p['seat']!=t['yourSeat']:
                            if not t['showdown'] or p['folded'] or not p['inHand']:
                                check(len(p['cards'])==0, 'No opponent card leakage on wire')
                            else: check(len(p['cards'])==p['count'], 'Showdown survivors revealed')
        except websockets.ConnectionClosed: pass
        except Exception as e: self.fault=e
    async def send(self, kind, **args):
        cid=args.pop('id',uuid.uuid4().hex)
        c=dict(type=kind, id=cid)
        if self.table: c.update(tableId=self.table['id'], revision=self.table['revision'], hand=self.table['hand'])
        c.update(args)
        start=len(self.messages)
        await self.ws.send(json.dumps(c))
        await wait_for(lambda: any(m.get('id')==cid and m['type'] in ('ack','error') for m in self.messages[start:]))
        return next(m for m in self.messages[start:] if m.get('id')==cid and m['type'] in ('ack','error'))
    async def close(self):
        await self.ws.close(); await self.task

async def action(clients, kind='call', **args):
    for _ in range(10):
        t=next(c.table for c in clients if c.table)
        if t['actor']<0: return None
        c=next(c for c in clients if c.table and c.table['yourSeat']==t['actor'])
        await wait_for(lambda:c.table and c.table['actor']==c.table['yourSeat'])
        seat=c.table['yourSeat']
        before=c.table['players'][seat]['cards'] if kind=='draw' and args.get('discards') else None
        result=await c.send(kind,**args)
        if result['type']=='ack':
            if before is not None:
                await wait_for(lambda:c.table and c.table['players'][seat]['cards']!=before)
                check(True,'Replacement cards update immediately after draw')
            return c
        check('Table changed' in result['message'], 'Only stale snapshot retry is permitted')
        await asyncio.sleep(.05)
    raise AssertionError('Action did not succeed')

async def finish_hand(clients, draw_all=False):
    hand=next(c.table['hand'] for c in clients if c.table)
    draw_limit_checked=False
    async with asyncio.timeout(20):
        while True:
            t=next(c.table for c in clients if c.table)
            if t['hand']!=hand or t['stage']=='Complete': return t
            if t['actor']<0: await asyncio.sleep(.025);continue
            if t['stage']=='Draw':
                if not draw_limit_checked:
                    actor=next(c for c in clients if c.table and c.table['yourSeat']==t['actor'])
                    result=await actor.send('draw',discards=[0,1,2])
                    check(result['type']=='error' and 'up to two' in result['message'],'Server rejects more than two discards')
                    draw_limit_checked=True
                await action(clients,'draw',discards=list(range(2)) if draw_all else [])
            else: await action(clients,'call')
            await asyncio.sleep(.04)

async def main():
    clients=[]
    try:
        bad=await Client('BadKey').connect(key='wrong')
        check(any(m['type']=='error' and 'invite' in m['message'] for m in bad.messages),'Invite enforcement')
        await bad.close()
        for i in range(6): clients.append(await Client('Smoke'+str(i)).connect())
        check(all(c.wallet_balance==10000 for c in clients),'New guest wallets receive the configured starting bank')
        check(all(r['maxSeats']==6 and r['available']==6 for r in clients[0].rows),'Empty lobby configuration')
        await clients[0].send('join',tableId='standard',entryMode='wait')
        await wait_for(lambda:clients[0].wallet_balance==9000)
        check(clients[0].wallet_balance==9000,'Joining debits the table buy-in from the bank')
        await asyncio.sleep(.35)
        waiting=clients[0].table
        check(waiting['stage']=='Waiting','One player cannot start')
        check(waiting['hand']==0 and waiting['board']==[] and waiting['pot']==0 and waiting['results']==[],'Empty table stays free of hand state')
        check(all(not p['inHand'] and p['count']==0 and not p['winner'] for p in waiting['players']),'Waiting players receive no cards or win')
        await clients[1].send('join',tableId='standard',entryMode='wait')
        await wait_for(lambda:clients[0].table['hand']==1)
        check(sum(p['inHand'] for p in clients[0].table['players'])==2,'Auto-start with two players')
        initial=clients[0].table
        await clients[2].send('join',tableId='standard',entryMode='post')
        await wait_for(lambda:clients[0].table['players'][2]['occupied'])
        t=clients[0].table
        check(not t['players'][2]['inHand'] and t['players'][2]['count']==0,'Mid-hand arrival cannot get cards')
        check(t['smallBlindSeat']==initial['smallBlindSeat'] and t['bigBlindSeat']==initial['bigBlindSeat'],'Mid-hand arrival cannot move blinds')
        for i in range(3,6): await clients[i].send('join',tableId='standard',entryMode='post')
        outsider=await Client('Outsider').connect()
        r=await outsider.send('join',tableId='standard',entryMode='post')
        check(r['type']=='error' and 'full' in r['message'],'Seventh player rejected')
        check(outsider.wallet_balance==10000,'Rejected full-table join does not debit the bank')
        await outsider.close()
        await wait_for(lambda:all(c.table and sum(p['occupied'] for p in c.table['players'])==6 for c in clients))
        t=clients[0].table
        wrong=next(c for c in clients if c.table['yourSeat']!=t['actor'])
        check((await wrong.send('call'))['type']=='error','Out-of-turn command rejected')
        actor=next(c for c in clients if c.table['yourSeat']==t['actor'])
        check((await actor.send('call',revision=-999))['type']=='error','Stale command rejected')
        result=await actor.send('raise',raiseTo=999999)
        check(result['type']=='error','Over-pot raise rejected by authoritative server')
        # Folding must not reveal the survivor's private cards.
        await action(clients,'fold')
        await wait_for(lambda:clients[0].table['stage']=='Complete')
        check(not clients[0].table['showdown'],'Uncontested win is not showdown')
        check(clients[0].table['pot']>0,'Uncontested pot remains visible after hand')
        check(any(p['winner'] and p['scoop'] for p in clients[0].table['players']),'Uncontested winner is marked as a scoop')
        await wait_for(lambda:clients[0].table['hand']==2)
        check(sum(p['inHand'] for p in clients[0].table['players'])==6,'Six connected humans admitted at next eligible boundary')
        check(clients[0].table['bigBlindSeat']==2 and clients[0].table['smallBlindSeat']==1,'Joining preserves original big-blind rotation')
        # Retry the exact accepted command ID: must not act twice.
        t=clients[0].table
        actor=next(c for c in clients if c.table['yourSeat']==t['actor'])
        cid=uuid.uuid4().hex
        check((await actor.send('call',id=cid))['type']=='ack','Valid action accepted')
        revision=actor.table['revision']
        check((await actor.send('call',id=cid))['type']=='ack','Repeated command acknowledged once')
        check(actor.table['revision']==revision,'Duplicate command has no state mutation')
        final=await finish_hand(clients,draw_all=True)
        check(final['showdown'] and len(final['board'])==5,'Showdown after complete board')
        check(final['pot']>0,'Showdown pot remains visible after hand')
        check(sum(p['stack'] for p in final['players'])==6000,'Multiplayer chips conserved with six full draws')
        await wait_for(lambda:all(c.table['stage']=='Complete' for c in clients))
        # Reconnect with an opaque token; same identity, seat and stack.
        old=clients[4]; token=old.token; wallet_id=old.wallet_id; oldseat=old.table['yourSeat']
        await old.close(); replacement=await Client('IgnoredName',token,wallet_id).connect(); clients[4]=replacement
        await wait_for(lambda:replacement.table is not None)
        check(replacement.table['yourSeat']==oldseat,'Reconnect retains seat')
        check(replacement.table['players'][oldseat]['username']=='IgnoredName','Reconnect can change the display name')
        check(replacement.wallet_balance==9000,'Reconnect retains the same guest wallet')
        # Folded or completed players can leave immediately.
        async with asyncio.timeout(20):
            while True:
                t=next(c.table for c in clients if c.table)
                if t['stage']=='Complete':
                    for c in clients:
                        if c.table:
                            result=await c.send('leave')
                            check(result['type']=='ack','Folded or completed player can leave')
                    break
                if t['actor']<0:
                    await asyncio.sleep(.025); continue
                if t['stage']=='Draw':
                    await action(clients,'draw',discards=[])
                else:
                    folded=await action(clients,'fold')
                    check((await folded.send('leave'))['type']=='ack','Folded player can leave without auto-folding')
        await wait_for(lambda:all(c.table is None for c in clients),timeout=12)

        queued=await Client('QueuedLeave').connect(); clients.append(queued)
        leaving=await Client('HandLeave').connect(); clients.append(leaving)
        partner=await Client('LeavePartner').connect(); clients.append(partner)
        await leaving.send('join',tableId='micro',entryMode='wait')
        await partner.send('join',tableId='micro',entryMode='wait')
        await wait_for(lambda:leaving.table and leaving.table['hand']==1)
        await queued.send('join',tableId='micro',entryMode='wait')
        await wait_for(lambda:queued.table and queued.table['players'][queued.table['yourSeat']]['occupied'])
        await wait_for(lambda:queued.wallet_balance==9800)
        check(not queued.table['players'][queued.table['yourSeat']]['inHand'],'Queued player is not dealt into current hand')
        check((await queued.send('leave'))['type']=='ack','Queued player can leave immediately')
        await wait_for(lambda:queued.table is None)
        check(queued.wallet_balance==10000,'Leaving before a hand cashes the full stack back into the bank')
        check(queued.table is None,'Queued seat is released immediately')
        check((await leaving.send('leave'))['type']=='ack','In-hand player can request deferred leave')
        await wait_for(lambda:leaving.table and leaving.table['players'][leaving.table['yourSeat']]['leaving'])
        await wait_for(lambda:partner.table and leaving.table and partner.table['revision']==leaving.table['revision'])
        check(leaving.table is not None,'In-hand player remains seated until hand completion')
        check(not leaving.table['players'][leaving.table['yourSeat']]['folded'],'Leave request does not fold the player')
        other_view=partner.table['players'][leaving.table['yourSeat']]
        check(not other_view['leaving'] and other_view['status']!='Leaving after hand','Deferred leave status is private to the requesting player')
        normal_action_completed=False
        async with asyncio.timeout(20):
            while True:
                t=leaving.table
                if t is None: break
                if t['actor']==leaving.table['yourSeat']:
                    if not normal_action_completed:
                        previous_revision=t['revision']
                        if t['stage']=='Draw':
                            result=await leaving.send('draw',discards=[])
                            check(result['type']=='ack','Pending leave can take a normal draw action')
                        else:
                            result=await leaving.send('call')
                            check(result['type']=='ack','Pending leave can take a normal betting action')
                        normal_action_completed=True
                        await wait_for(lambda:leaving.table is None or leaving.table['revision']>previous_revision)
                        check(leaving.table is None or leaving.table['players'][leaving.table['yourSeat']]['leaving'],'Normal action keeps leave pending')
                    else:
                        check((await leaving.send('fold'))['type']=='ack','Pending leave can fold on its turn')
                        break
                if t['actor']<0:
                    await asyncio.sleep(.025); continue
                if leaving.table is not None and leaving.table['actor']!=leaving.table['yourSeat']:
                    if leaving.table['stage']=='Draw': await action([leaving,partner],'draw',discards=[])
                    else: await action([leaving,partner],'call')
        check(normal_action_completed,'Leave request preserves normal turn options')
        await wait_for(lambda:leaving.table is None)
        if partner.table['stage']!='Waiting': await finish_hand([partner])
        check(leaving.table is None,'In-hand player is removed after hand completion')
        check((await partner.send('leave'))['type']=='ack','Remaining player can leave completed table')
        await wait_for(lambda:partner.table is None)

        showdown_leaving=await Client('ShowdownLeave').connect(); clients.append(showdown_leaving)
        showdown_partner=await Client('ShowdownPartner').connect(); clients.append(showdown_partner)
        await showdown_leaving.send('join',tableId='micro',entryMode='wait')
        await showdown_partner.send('join',tableId='micro',entryMode='wait')
        await wait_for(lambda:showdown_leaving.table and showdown_leaving.table['players'][showdown_leaving.table['yourSeat']]['inHand'])
        check((await showdown_leaving.send('leave'))['type']=='ack','Showdown player can request deferred leave')
        await finish_hand([showdown_leaving,showdown_partner])
        await wait_for(lambda:showdown_partner.table and showdown_partner.table['stage']=='Complete')
        check(showdown_leaving.table is not None,'Showdown leaver remains seated during the cooldown')
        own_seat=showdown_leaving.table['yourSeat']
        check(showdown_leaving.table['players'][own_seat]['leaving'] and showdown_leaving.table['board'] and showdown_leaving.table['results'],'Showdown leaver can see the board and winner')
        await asyncio.sleep(.2)
        check(showdown_leaving.table is not None and showdown_leaving.table['stage']=='Complete','Showdown leaver remains through the cooldown')
        await wait_for(lambda:showdown_leaving.table is None,timeout=5)
        check(showdown_partner.table['stage']=='Waiting' and showdown_partner.table['board']==[] and showdown_partner.table['pot']==0,'Single-player table clears showdown state and waits for players')
        check((await showdown_partner.send('leave'))['type']=='ack','Last waiting player can leave')
        await wait_for(lambda:showdown_partner.table is None)
        check(all(c.fault is None for c in clients),'No privacy assertion failures in readers')
        print(f'PASS: {assertions} multiplayer assertions; six real WebSocket clients, authorization, lobby, joins, actions, privacy, full draws, showdown, duplicate commands, reconnect and leave.')
    finally:
        for c in clients:
            try: await c.close()
            except Exception: pass

if __name__=='__main__':asyncio.run(main())
