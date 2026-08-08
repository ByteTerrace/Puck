import json, collections, sys

names = sorted(set(open('.runs/verbs-win.txt', encoding='utf-8').read().split()))
al = open('.runs/afford-line-new.txt', encoding='utf-8').read()
s = al.index('[', al.index('world.affordances:'))
arr, _ = json.JSONDecoder().raw_decode(al[s:])
aff = {a['name']: a for a in arr}

BUILTIN = ['help', 'wire.ack', 'wire.errors']
GENERATED = [n for n in names if n.startswith('channel.')]

READ = """audio.emitters audio.state speaker.state chat.read
editor.creations editor.status editor.sculpt.status
identity.list identity.show identity.state identity.writebacks
player.bindings player.channels player.state player.sticks player.targets player.where
replay.list replay.status screen.links screen.peek screen.state
storage.credential storage.status
world.addons world.affordances world.attachments world.cameras world.collision.probe
world.collision.status world.contacts world.device-profiles world.devices world.faces
world.fps world.gpu world.grants world.groups world.host world.hud world.hud.template
world.inhabitants world.input-holds world.instance.seats world.instance.status
world.interactions world.looks world.parked world.peers world.placement.get world.players
world.portals world.properties world.references world.refusals world.rules world.screens world.view.orbit
world.speakers world.state world.status world.view.state world.why""".split()

IO = """backend quit capture.start capture.status capture.stop
replay.cancel replay.record replay.stop replay.verify
storage.pull storage.push world.save world.screenshot world.sdf.load world.wait""".split()

# ---- SUGAR, grouped by the reason it is sugar. value = (replacement, tier) ----
SUGAR = {}


def add(group, tier, items):
    for n, repl in items.items():
        assert n not in SUGAR, n
        SUGAR[n] = (group, tier, repl)


add('A. self-declared "Console sugar" RMW', 1, {
    'world.host.tune': 'world.host.set <json>',
    'world.kit.tune': 'world.kit.set <kit-json>',
    'world.look.tune': 'world.look.set <look-json>',
})

add('B. RMW field wrapper over a whole-row upsert', 1, {
    'world.collision.gradient': 'world.collision <json>',
    'world.collision.requirements': 'world.collision <json>',
    'world.collision.skin': 'world.collision <json>',
    'world.collision.slope': 'world.collision <json>',
    'world.kit.collider': 'world.kit.set <kit-json>',
    'world.kit.program': 'world.kit.set <kit-json>',
    'world.kit.response': 'world.kit.set <kit-json>',
    'world.placement.face': 'world.placement.set <json>',
    'world.placement.inhabit': 'world.placement.set <json>',
    'world.hud.element.set': 'world.hud.panel.set <json> (elements ride the panel row)',
    'world.hud.element.remove': 'world.hud.panel.set <json> (elements ride the panel row)',
})

add('C. door-not-type split (one act, two verbs by operand kind)', 1, {
    'world.state.cell.text': 'world.state.cell.set <row> <key> <value> (dispatch on the row\'s declared kind)',
})

add('D. stepped/cycled twin of an arg-taking verb (bind via CommandValue, the player.claim precedent)', 1, {
    'editor.sculpt.zoom.in': 'editor.sculpt.zoom in   (verb ALREADY accepts in|out|<distance>)',
    'editor.sculpt.zoom.out': 'editor.sculpt.zoom out  (verb ALREADY accepts in|out|<distance>)',
    'editor.sculpt.smooth.up': 'editor.sculpt.smooth up   (widen to <v|up|down>)',
    'editor.sculpt.smooth.down': 'editor.sculpt.smooth down (widen to <v|up|down>)',
    'editor.sculpt.material.next': 'editor.sculpt.material next (widen to <slot|next|prev>, as editor.sculpt.primitive already is)',
    'editor.sculpt.material.prev': 'editor.sculpt.material prev (same widening)',
    'editor.sculpt.grow': 'editor.sculpt.scale grow   (widen to <s|x y z|grow|shrink>)',
    'editor.sculpt.shrink': 'editor.sculpt.scale shrink (same widening)',
    'editor.sculpt.frame.next': 'editor.sculpt.frame next (widen to <n|next|prev>)',
    'editor.sculpt.frame.prev': 'editor.sculpt.frame prev (same widening)',
    'editor.sculpt.next': 'editor.sculpt.select next (widen to <id|name|next|prev>)',
    'editor.sculpt.prev': 'editor.sculpt.select prev (same widening)',
    'editor.next': 'editor.select next (widen to [<section> <id> | next | prev | none])',
    'editor.prev': 'editor.select prev (same widening)',
    'editor.deselect': 'editor.select none (same widening)',
    'editor.fly': 'editor.camera fly   (widen the toggle to [fly|orbit])',
    'editor.orbit': 'editor.camera orbit (same widening)',
    'editor.faster': 'editor.cam.speed faster (widen to <unitsPerSecond|faster|slower>)',
    'editor.slower': 'editor.cam.speed slower (same widening)',
    'editor.sculpt.chain.define': 'editor.sculpt.chain (no-arg = define a limb from the selection)',
    'player.run': 'player.fly <f> <s> 0 <turn> 0 0 <sec>  (fly is the strict 6DOF superset)',
})

add('E. planar shorthand over a general pose door (stale-read RMW)', 2, {
    'player.face': "player.pose - - - <deg> - -   (widen player.pose with '-' = hold current)",
    'player.warp': "player.pose <x> - <z> - - -   (same widening)",
})

add('F. per-kind verb family collapsing to one verb + subcommand token', 2, {
    'screen.camera': 'screen.source <index> camera',
    'screen.capture': 'screen.source <index> capture <windowTitle...>',
    'screen.desktop': 'screen.source <index> desktop [monitorIndex]',
    'screen.qr': 'screen.source <index> qr [payload] [ecLevel] [quietZone]',
    'screen.view': 'screen.source <index> view <cameraName>',
    'view.camera': 'view.override camera <name|auto>',
    'view.layout': 'view.override layout <name|auto>',
    'world.kit.assign': 'world.assign kits r1|cycle <name>...',
    'world.look.assign': 'world.assign looks r1|cycle <name>...',
})

add('G. per-target duplication of an existing verb surface', 2, {
    'world.instance.seat.enter': 'player.join <profile> [n] instance:<name>',
    'world.instance.seat.leave': 'player.leave <n> instance:<name>',
    'world.instance.seat.face': 'player.face <deg> <n> instance:<name>',
    'world.instance.seat.run': 'player.run/fly ... <n> instance:<name>',
    'world.instance.seat.stop': 'player.stop <n> instance:<name>',
    'world.instance.seat.warp': 'player.warp <x> <z> <n> instance:<name>',
    'world.instance.seat.where': 'player.where <n> instance:<name>',
})

add('J. per-field setter family on one subject (same shape as the *.tune verdict)', 2, {
    'editor.sculpt.bend': 'editor.sculpt.set bend <v>',
    'editor.sculpt.dilate': 'editor.sculpt.set dilate <v>',
    'editor.sculpt.onion': 'editor.sculpt.set onion <v>',
    'editor.sculpt.twist': 'editor.sculpt.set twist <v>',
    'editor.sculpt.rotate': 'editor.sculpt.set rotate <yaw> <pitch> <roll>',
    'editor.sculpt.move': 'editor.sculpt.set move <x> <y> <z>',
    'editor.sculpt.nudge': 'editor.sculpt.set nudge <dx> <dy> <dz>',
    'editor.sculpt.rename': 'editor.sculpt.set name <name>',
})

add('K. editor twin submitting a mutation an existing document verb already submits', 2, {
    'editor.speaker.move': 'world.speaker.set <json>   (both submit UpsertSpeaker)',
    'editor.speaker.gain': 'world.speaker.set <json>   (both submit UpsertSpeaker)',
    'editor.speaker.channel': 'world.speaker.set <json>   (both submit UpsertSpeaker)',
    'editor.speaker.radius': 'world.speaker.set <json>   (both submit UpsertSpeaker)',
    'editor.speaker.delete': 'world.speaker.remove <name> (both submit RemoveSpeaker)',
})

add('L. relative twin of an absolute verb (the ledgered stale-read race class)', 1, {
    'editor.nudge': 'editor.move <x> <y> <z> (absolute; retires this row from the stale-read defect ledger)',
})

add('H. dev harness duplicating a shipped door', 2, {
    'identity.deliver': 'chat.whisper (the real door). Its ONLY extra ability is forging an arbitrary source id — a capability the authority model denies everywhere else.',
})

# ---- Tier 3: the document row/section mutation family -> 2 general verbs ----
ROWPAIR_SECTIONS = ['world.addon', 'world.bindings', 'world.camera', 'world.creation',
                    'world.grant', 'world.group.kind', 'world.hud.panel', 'world.interaction',
                    'world.kit', 'world.link', 'world.look', 'world.patch', 'world.placement',
                    'world.property', 'world.rule', 'world.screen', 'world.speaker',
                    'world.state', 'world.tune', 'world.view.layout']
SECTION_REPLACE = ['world.view.rig', 'world.view.look', 'world.audio.set', 'world.authoring.set', 'world.collision', 'world.host.set',
                   'world.hud.defaults.set', 'world.input-hold.set', 'world.motion.set',
                   'world.render.defaults', 'world.spawns.set']

TIER3 = {}
for sec in ROWPAIR_SECTIONS:
    for suffix, repl in (('.set', 'world.row.set <section> <json>'), ('.remove', 'world.row.remove <section> <key>')):
        n = sec + suffix
        if n in names:
            TIER3[n] = repl
for n in SECTION_REPLACE:
    TIER3[n] = 'world.row.set <section> <json>  (keyless singleton section)'

# ---- everything else is a MUTATION DOOR / bound control ----
assigned = set(BUILTIN) | set(GENERATED) | set(READ) | set(IO) | set(SUGAR)
DOOR = [n for n in names if n not in assigned]

# sanity
for group, lst in (('READ', READ), ('IO', IO), ('SUGAR', list(SUGAR)), ('TIER3', list(TIER3))):
    for n in lst:
        if n not in names:
            print('!! not a registered verb:', group, n)

print('TOTAL registered names (windowed, play):', len(names))
print()
print('  BUILT-IN (registry infrastructure)   :', len(BUILTIN))
print('  GENERATED (per declared channel)     :', len(GENERATED))
print('  READ-BACK                            :', len(READ))
print('  ENGINE I/O                           :', len(IO))
print('  MUTATION DOOR / bound control        :', len(DOOR))
print('  SUGAR (tier 1+2 kill list)           :', len(SUGAR))
print('  -----')
print('  sum                                  :', len(BUILTIN) + len(GENERATED) + len(READ) + len(IO) + len(DOOR) + len(SUGAR))
print()
t1 = [n for n, v in SUGAR.items() if v[1] == 1]
t2 = [n for n, v in SUGAR.items() if v[1] == 2]
print('tier 1 (pure sugar, no design change) :', len(t1))
print('tier 2 (collapse to verb+token)       :', len(t2))
print('tier 3 (document row/section family)  :', len(TIER3), '-> 2 general verbs')
print()
# net arithmetic: tier2 groups F/G add replacement verbs
NEW_VERBS_T2 = ['screen.source', 'view.override', 'world.assign', 'editor.sculpt.set']
NEW_VERBS_T3 = ['world.row.set', 'world.row.remove']
overlap = set(TIER3) & set(SUGAR)
print('tier3 overlaps already-counted sugar  :', sorted(overlap))
t3_only = [n for n in TIER3 if n not in SUGAR]
net1 = len(t1)
net2 = len(t2) - len(NEW_VERBS_T2)
net3 = len(t3_only) - len(NEW_VERBS_T3)
print()
print(f'NET reduction  tier 1        : -{net1}   ->  {len(names)-net1}')
print(f'NET reduction  tier 1+2      : -{net1+net2}   ->  {len(names)-net1-net2}')
print(f'NET reduction  tier 1+2+3    : -{net1+net2+net3}   ->  {len(names)-net1-net2-net3}')
print()
print('bindable verbs on the kill list (must survive as a bound control under the collapsed name):')
for n in sorted(SUGAR):
    if aff.get(n, {}).get('bindable'):
        print('   ', n)

with open('.runs/ledger.tsv', 'w', encoding='utf-8') as f:
    f.write('verb\tclass\trouting\tbindable\tgroup\ttier\treplacement\n')
    for n in names:
        a = aff.get(n, {})
        r = a.get('routing', 'builtin')
        b = 'yes' if a.get('bindable') else 'no'
        if n in SUGAR:
            g, t, repl = SUGAR[n]
            f.write(f'{n}\tSUGAR\t{r}\t{b}\t{g}\t{t}\t{repl}\n')
        elif n in TIER3:
            f.write(f'{n}\tSUGAR\t{r}\t{b}\tI. document row/section family\t3\t{TIER3[n]}\n')
        else:
            cls = ('BUILT-IN' if n in BUILTIN else 'GENERATED' if n in GENERATED
                   else 'READ-BACK' if n in READ else 'ENGINE-IO' if n in IO else 'DOOR')
            f.write(f'{n}\t{cls}\t{r}\t{b}\t\t\t\n')
print()
print('wrote .runs/ledger.tsv')
