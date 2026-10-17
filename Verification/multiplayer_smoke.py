"""Run against a local server. Requires Python 3.11+ and websockets.
Start with DRAWMAHA_SHOWDOWN_SECONDS=1 and DRAWMAHA_RECONNECT_SECONDS=3.
"""
import asyncio, json, sys, uuid
import websockets

URL = sys.argv[1] if len(sys.argv) > 1 else 'ws://127.0.0.1:8080/ws'
PROTOCOL = 12
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
        self.lobby_received = False
        self.messages = []
        self.fault = None
    async def connect(self):
        self.ws = await websockets.connect(URL)
        await self.ws.send(json.dumps(dict(type='hello', username=self.name, token=self.token, walletId=self.wallet_id, avatar=2, protocol=PROTOCOL)))
        self.task = asyncio.create_task(self.reader())
        await wait_for(lambda: self.token or any(m['type']=='error' for m in self.messages))
        if self.token: await wait_for(lambda: self.lobby_received)
        return self
    async def reader(self):
        try:
            async for frame in self.ws:
                m=json.loads(frame); self.messages.append(m)
                if m['type']=='welcome':
                    self.token=m['token']; self.wallet_balance=m['walletBalance']
                    self.auto_top_up=m.get('autoTopUpEnabled',False)
                    self.auto_top_up_big_blinds=m.get('autoTopUpBigBlinds',100)
                    self.always_top_up_max=m.get('alwaysTopUpMax',False)
                if m['type']=='topUpSettings':
                    self.auto_top_up=m['autoTopUpEnabled']
                    self.auto_top_up_big_blinds=m['autoTopUpBigBlinds']
                    self.always_top_up_max=m['alwaysTopUpMax']
                if m['type']=='wallet': self.wallet_balance=m['walletBalance']
                if m['type']=='lobby': self.rows=m['tables']; self.lobby_received=True
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
        try: await wait_for(lambda: any(m.get('id')==cid and m['type'] in ('ack','error') for m in self.messages[start:]))
        except TimeoutError as e: raise AssertionError(f'{kind} command timed out; received={self.messages[start:]}; fault={self.fault}; socket={self.ws.state}') from e
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
        guest=await Client('NoInvite').connect()
        check(len(guest.rows)==0, 'Guests reach an empty lobby without a server invite code')
        await guest.close()
        public_host=await Client('PublicHost').connect(); clients.append(public_host)
        result=await public_host.send('create',tableName='Public Test',smallBlind=1,bigBlind=2,minBuyIn=200,maxBuyIn=800,isPublic=True)
        check(result['type']=='ack','Public table creation is acknowledged')
        public_id=result['tableId']
        check(public_id and public_host.table is not None and public_host.table['yourSeat']==-1
            and public_host.wallet_balance==10000,
            'Creating a table shows its preview without buying in or seating immediately')
        await wait_for(lambda:any(row['id']==public_id for row in public_host.rows))
        public_row=next(row for row in public_host.rows if row['id']==public_id)
        check(public_row['isPublic'] and public_row['available']==5 and 'inviteCode' not in public_row,
            'Public table is listed with a reserved creator seat and no invite-code field')
        cancelled=await Client('CancelledCreate').connect(); clients.append(cancelled)
        cancelled_result=await cancelled.send('create',tableName='Cancelled Table',smallBlind=1,bigBlind=2,
            minBuyIn=200,maxBuyIn=800,isPublic=True)
        cancelled_id=cancelled_result['tableId']
        check(cancelled_id and cancelled.wallet_balance==10000 and cancelled.table is not None
            and cancelled.table['yourSeat']==-1,
            'New table preview does not debit the creator before Accept')
        await cancelled.send('cancelCreate')
        await wait_for(lambda:cancelled.table is None and all(row['id']!=cancelled_id for row in cancelled.rows))
        check(cancelled.wallet_balance==10000,'Cancelling create releases the reserved seat without a wallet transfer')
        await cancelled.close(); clients.remove(cancelled)
        await public_host.send('join',tableId=public_id,entryMode='wait',buyInAmount=200)
        await wait_for(lambda:public_host.table is not None and public_host.wallet_balance==9800)
        public_joiner=await Client('PublicJoiner').connect(); clients.append(public_joiner)
        await public_joiner.send('join',tableId=public_id,entryMode='wait',buyInAmount=400)
        await wait_for(lambda:public_joiner.table is not None and public_joiner.wallet_balance==9600)
        check(public_joiner.table['id']==public_id,'Public table accepts a join without an invite code')
        check(public_joiner.table['players'][public_joiner.table['yourSeat']]['stack']==400,'Joiner-selected buy-in becomes their stack')
        await wait_for(lambda:public_host.table['stage']!='Waiting' and public_joiner.table['stage']!='Waiting')
        await finish_hand([public_host,public_joiner])
        await public_host.send('leave'); await public_joiner.send('leave')
        await wait_for(lambda:public_host.table is None and public_joiner.table is None)
        await wait_for(lambda:len(public_host.rows)==0)
        await public_host.close(); await public_joiner.close()
        clients.clear()
        for i in range(6): clients.append(await Client('Smoke'+str(i)).connect())
        check(all(c.wallet_balance==10000 for c in clients),'New guest wallets receive the configured starting bank')
        check(len(clients[0].rows)==0,'Tables disappear from the lobby after their last player leaves')
        result=await clients[0].send('create',tableName='Smoke Private',smallBlind=5,bigBlind=10,minBuyIn=1000,maxBuyIn=3000,isPublic=False,inviteCode='FELT-2026')
        check(result['type']=='ack','Private table creation is acknowledged')
        main_table_id=result['tableId']
        check(main_table_id and clients[0].table is not None and clients[0].table['yourSeat']==-1
            and clients[0].wallet_balance==10000,
            'Private table creator sees the preview but is not seated or charged before accepting a buy-in')
        await wait_for(lambda:any(row['id']==main_table_id for row in clients[0].rows))
        private_row=next(row for row in clients[0].rows if row['id']==main_table_id)
        check(not private_row['isPublic'] and private_row['available']==5 and 'inviteCode' not in private_row,
            'Private table appears with a reserved creator seat without exposing its invite code')
        wrong_code=await Client('WrongCode').connect()
        rejected=await wrong_code.send('join',tableId=main_table_id,entryMode='wait',buyInAmount=1000,inviteCode='WRONG-42')
        check(rejected['type']=='error' and 'invite code' in rejected['message'].lower(),'Private table rejects an incorrect invite code')
        check(wrong_code.wallet_balance==10000,'Rejected private join does not debit the wallet')
        rejected=await wrong_code.send('join',tableId=main_table_id,entryMode='wait',buyInAmount=1000)
        check(rejected['type']=='error' and 'invite code' in rejected['message'].lower(),'Private table rejects a missing invite code')
        rejected=await wrong_code.send('join',tableId=main_table_id,entryMode='wait',buyInAmount=3001,inviteCode='FELT-2026')
        check(rejected['type']=='error' and 'minimum and maximum' in rejected['message'],'Server rejects a buy-in above the table maximum')
        check(wrong_code.wallet_balance==10000,'Rejected joins leave the guest wallet untouched')
        await wrong_code.close()
        await clients[0].send('join',tableId=main_table_id,entryMode='wait',buyInAmount=1000,inviteCode='FELT-2026')
        await wait_for(lambda:clients[0].table is not None and clients[0].wallet_balance==9000)
        check(clients[0].table['players'][clients[0].table['yourSeat']]['stack']==1000,
            'Accepting the selected creator buy-in seats the creator and debits the bank')
        rejected=await clients[0].send('cancelCreate')
        check(rejected['type']=='error' and clients[0].table is not None,
            'Cancel-create cannot eject a player after their accepted buy-in')
        await asyncio.sleep(.35)
        waiting=clients[0].table
        check(waiting['stage']=='Waiting' and waiting['hand']==0,'One seated player cannot start a hand')
        check(waiting['board']==[] and waiting['pot']==0 and waiting['results']==[],'Empty table stays free of hand state')
        check(all(not p['inHand'] and p['count']==0 and not p['winner'] for p in waiting['players']),'Waiting players receive no cards or win')
        check(all(r['maxSeats']==6 and r['available']==5 for r in clients[0].rows if r['id']==main_table_id),'Created table publishes live seat availability')
        await clients[1].send('join',tableId=main_table_id,entryMode='wait',buyInAmount=1000,inviteCode='felt-2026')
        await wait_for(lambda:clients[0].table['hand']==1)
        check(sum(p['inHand'] for p in clients[0].table['players'])==2,'Auto-start with two players')
        await clients[0].send('autoTopUp',enabled=True,autoTopUpBigBlinds=300,alwaysTopUpMax=False)
        initial=clients[0].table
        await clients[2].send('join',tableId=main_table_id,entryMode='post',buyInAmount=1000,inviteCode='FELT-2026')
        await wait_for(lambda:clients[0].table['players'][2]['occupied'])
        t=clients[0].table
        check(not t['players'][2]['inHand'] and t['players'][2]['count']==0,'Mid-hand arrival cannot get cards')
        check(t['smallBlindSeat']==initial['smallBlindSeat'] and t['bigBlindSeat']==initial['bigBlindSeat'],'Mid-hand arrival cannot move blinds')
        await clients[2].send('addOn',addOnAmount=5000)
        await wait_for(lambda:clients[2].table['players'][2]['pendingAddOn']==2000)
        check(clients[2].table['players'][2]['stack']==1000,
            'Oversized add-on is capped at table capacity and does not move chips mid-hand')
        await clients[3].send('join',tableId=main_table_id,entryMode='post',buyInAmount=1000,inviteCode='FELT-2026')
        await clients[3].send('autoTopUp',enabled=True,autoTopUpBigBlinds=50,alwaysTopUpMax=True)
        check(clients[3].auto_top_up and clients[3].always_top_up_max,
            'Always top up to max is persisted as an enabled top-up setting')
        for i in range(4,6): await clients[i].send('join',tableId=main_table_id,entryMode='post',buyInAmount=1000,inviteCode='FELT-2026')
        await wait_for(lambda:clients[2].wallet_balance==7000 and clients[3].wallet_balance==7000)
        check(clients[1].wallet_balance==9000 and clients[4].wallet_balance==9000 and clients[5].wallet_balance==9000,
            'Only accepted buy-ins and queued chip funding debit main banks')
        await asyncio.sleep(.35)
        waiting=clients[0].table
        outsider=await Client('Outsider').connect()
        r=await outsider.send('join',tableId=main_table_id,entryMode='post',buyInAmount=1000,inviteCode='FELT-2026')
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
        await wait_for(lambda:clients[0].table['players'][0]['stack'] + clients[0].table['players'][0]['bet'] == 3000
            and clients[0].table['players'][2]['stack'] + clients[0].table['players'][2]['bet'] == 3000
            and clients[0].table['players'][3]['stack'] + clients[0].table['players'][3]['bet'] == 3000)
        check(clients[0].wallet_balance < 9000 and clients[2].wallet_balance==7000 and clients[3].wallet_balance==7000,
            'Top-up threshold, max mode, and capped add-on transfer funds at the hand boundary')
        check(clients[2].table['players'][2]['pendingAddOn']==0,
            'Queued add-on clears after it is applied to the next hand')
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
        check(sum(c.wallet_balance for c in clients[:6]) + sum(p['stack'] for p in final['players']) == 60000,
            'Multiplayer chips conserved after bank-funded add-ons and top-ups')
        await wait_for(lambda:all(c.table['stage']=='Complete' for c in clients))
        # Reconnect with an opaque token; same identity, seat and stack.
        old=clients[0]; token=old.token; wallet_id=old.wallet_id; oldseat=old.table['yourSeat']
        await old.close(); replacement=await Client('IgnoredName',token,wallet_id).connect(); clients[0]=replacement
        await wait_for(lambda:replacement.table is not None)
        check(replacement.table['yourSeat']==oldseat,'Reconnect retains seat')
        check(replacement.table['players'][oldseat]['username']=='IgnoredName','Reconnect can change the display name')
        check(replacement.wallet_balance < 9000,'Reconnect retains the same guest wallet')
        check(replacement.auto_top_up and replacement.auto_top_up_big_blinds==300,
            'Auto top-up settings persist when the player reconnects')
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
        result=await leaving.send('create',tableName='Queued Leave',smallBlind=1,bigBlind=2,minBuyIn=200,maxBuyIn=1000,isPublic=False,inviteCode='LEAVE-42')
        check(result['type']=='ack','Private queued-leave table is created')
        leave_table_id=result['tableId']
        await leaving.send('join',tableId=leave_table_id,entryMode='wait',buyInAmount=200,inviteCode='LEAVE-42')
        await wait_for(lambda:leaving.table is not None)
        await partner.send('join',tableId=leave_table_id,entryMode='wait',buyInAmount=200,inviteCode='LEAVE-42')
        await wait_for(lambda:leaving.table and leaving.table['hand']==1)
        await queued.send('join',tableId=leave_table_id,entryMode='wait',buyInAmount=200,inviteCode='LEAVE-42')
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
        result=await showdown_leaving.send('create',tableName='Showdown Leave',smallBlind=1,bigBlind=2,minBuyIn=200,maxBuyIn=1000,isPublic=False,inviteCode='SHOW-042')
        check(result['type']=='ack','Private showdown table is created')
        showdown_table_id=result['tableId']
        await showdown_leaving.send('join',tableId=showdown_table_id,entryMode='wait',buyInAmount=200,inviteCode='SHOW-042')
        await wait_for(lambda:showdown_leaving.table is not None)
        await showdown_partner.send('join',tableId=showdown_table_id,entryMode='wait',buyInAmount=200,inviteCode='SHOW-042')
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
