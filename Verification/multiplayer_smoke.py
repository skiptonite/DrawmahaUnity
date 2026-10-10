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
    def __init__(self, name, token=None):
        self.name, self.token = name, token
        self.table = None
        self.rows = []
        self.messages = []
        self.fault = None
    async def connect(self, key=KEY):
        self.ws = await websockets.connect(URL)
        await self.ws.send(json.dumps(dict(type='hello', username=self.name, token=self.token, avatar=2, accessKey=key)))
        self.task = asyncio.create_task(self.reader())
        await wait_for(lambda: self.token or any(m['type']=='error' for m in self.messages))
        if key == KEY: await wait_for(lambda: len(self.rows)==3)
        return self
    async def reader(self):
        try:
            async for frame in self.ws:
                m=json.loads(frame); self.messages.append(m)
                if m['type']=='welcome': self.token=m['token']
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
        check(all(r['maxSeats']==6 and r['available']==6 for r in clients[0].rows),'Empty lobby configuration')
        await clients[0].send('join',tableId='standard',entryMode='wait')
        await asyncio.sleep(.35)
        check(clients[0].table['stage']=='Waiting','One player cannot start')
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
        await outsider.close()
        await wait_for(lambda:all(c.table and sum(p['occupied'] for p in c.table['players'])==6 for c in clients))
        t=clients[0].table
        wrong=next(c for c in clients if c.table['yourSeat']!=t['actor'])
        check((await wrong.send('call'))['type']=='error','Out-of-turn command rejected')
        actor=next(c for c in clients if c.table['yourSeat']==t['actor'])
        leave=await actor.send('leave')
        check(leave['type']=='error' and 'Fold before leaving' in leave['message'],'Live player cannot leave before folding')
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
        old=clients[4]; token=old.token; oldseat=old.table['yourSeat']
        await old.close(); replacement=await Client('IgnoredName',token).connect(); clients[4]=replacement
        await wait_for(lambda:replacement.table is not None)
        check(replacement.table['yourSeat']==oldseat,'Reconnect retains seat')
        check(replacement.table['players'][oldseat]['username']=='Smoke4','Reconnect cannot change identity')
        # Fold before leaving; a pending leave must not auto-fold an active player.
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
        check(all(c.fault is None for c in clients),'No privacy assertion failures in readers')
        print(f'PASS: {assertions} multiplayer assertions; six real WebSocket clients, authorization, lobby, joins, actions, privacy, full draws, showdown, duplicate commands, reconnect and leave.')
    finally:
        for c in clients:
            try: await c.close()
            except Exception: pass

if __name__=='__main__':asyncio.run(main())
