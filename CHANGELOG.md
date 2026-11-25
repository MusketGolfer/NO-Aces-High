# Changelog

## 1.2.0

Everything since 1.1 (v0.9.7).

### Intros
- **Smoother camera.** The "Tails" pan now glides along the fins in the order they sit across the formation, on a smooth
  curve, instead of jumping from jet to jet (it was most noticeable in finger-four and box formations).
- **Framing fits the flight.** The formation-from-behind shot now backs off and zooms to fit the whole squadron, from a
  two-ship to a sixteen-ship.
- **Three new intro shot lists**, also in the random rotation:
  - **Escort:** riding behind the lead with the wingmen in frame, a rack focus from a wingman's wingtip onto the lead,
    then straight down on the formation.
  - **Silhouette:** the flight crossing the sun, a slow push-in onto the lead's canopy, then the formation from behind.
  - **Overhead:** straight down on the formation as it turns beneath the camera, a beam pass, then riding behind the lead.

### Aces
- **Ambient radio chatter.** Aces now talk now and then without anything in particular happening: banter on the way in
  and during the fight, never during the intro or over another radio call. Squadrons can write their own
  (`chatterIngress`, `chatterFight`, or `chatter` for both); timing and chance are in *8. Sound*.
- **Smarter running dark.** A flight only switches its radars and jammers off when that will actually take it off the
  scope: no radar or spotter of yours can see it and it isn't inside any of your radars' coverage. Under an AWACS, ship
  or ground radar, it now stays lit, since going dark there changed nothing.

### Random squadrons
- **Five new naming traditions:** Mercenary contractors ("Sable Company"), Legions ("Fulminata", Roman-numeral
  callsigns), Honour Guards ("The Iron Guard", heraldic callsigns), Experimental test units ("Sigma Flight", "Project
  Nightjar") and Militias ("Coast Irregulars"). Each side favours its own.
- **Three times the names:** every word list behind squadron names, titles, callsigns, places and pilot names has
  roughly tripled, so random squadrons repeat far less often.
- **Eight new emblem frames** (diamond, hexagon, delta, cross, sunburst, patch, lightning roundel, spade), each with one
  of eight centre motifs: 64 new designs.
- **Redesigned roundel:** a notched gold bezel, a domed disc in the squadron colour, and the same centre motifs.

### Look and feel
- **Sharp emblems.** Generated emblems are now painted at four times the resolution (in the background, so no hitch),
  and all emblems stay crisp when drawn small.
- **New HUD defaults:** the health bars are narrower (66% of the screen) and slimmer, and the threat gauge sits lower so
  it no longer overlaps UI Plus's default subtitle position. Your own placement is kept if you have moved them.

### Squadrons
- **Grey Wolves** keep their roundel emblem (now in the new design).

### For squadron makers
- The squadron guide lists the new `naming` traditions, `emblemShape` frames, `introShots` lists and chatter line keys.
