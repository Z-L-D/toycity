# Beta 4.1, part 3: storms, the weather station and the radar

Zac, 2026-11-03: a weather station and a doppler radar readout in the style of College of DuPage's NEXRAD pages, base
reflectivity at tilt 1 at the least, with local wind, temperature and humidity; and, as a side game idea, drawing a
storm with a direction and watching it move through. Zac, 2026-11-05: the radar goes in before the release candidate.

## What changed

- **Storm cells (`Storms.cs`).**
  - The fronts still set the day's weather, and the day clock's showers still say in which hours it rains or snows on
    the city. The storm cells are drawn to match them:
    - each shower hour brings a cell down its track over the middle of the city, long enough to cover the whole hour;
    - two more cells an hour pass by, nearer or further, clear of the city.
  - The cells move on the day's steering wind, mostly from the west. A winter storm is quicker and a heatwave
    sluggish.
  - Each cell is born and dies over three hours, strongest in its middle.
  - A heatwave brings afternoon thunderstorms out in the country, slow and never over the city.
  - All of it is worked out from the day, the hour and the weather, so none of it is saved and every player sees the
    same sky.
- **Storms the mayor sets going** (a new protocol action, `PlaceStorm`).
  - A storm starts anywhere within the radar's 40 km, heads any way at up to 120 km/h, and is light, heavy or severe.
  - It lasts six clock hours, building over the first and dying away over the last.
  - It snows from December to February. A severe storm from May to September is a thunderstorm with hail in its core.
  - Where it passes over the city it rains or snows there: the roads get wet, snow lies, and the road weather says so.
    The sim looks every six clock minutes through each step, so a quick storm on a quick calendar doesn't jump over
    the city.
  - Placed storms are saved with the city (save format 21) and let go once they've passed.
- **The weather station** (`World.Station`) reads:
  - the temperature, on the month's normal day curve, warmer in a heatwave and colder under rain;
  - the dew point and the relative humidity worked out from them;
  - the wind, blowing the way the storms go, stronger in a gale and under a shower;
  - the pressure, lower under a front;
  - the rain falling on the middle of the city now, from the radar's reflectivity by Marshall and Palmer
    (Z = 200 R^1.6) for rain and Z = 2000 R² for snow;
  - what has fallen since midnight.
- **The radar (`ClientModel/Graphics/Radar.cs`, ported word for word to `web/src/radar.ts`).**
  - The server sends the cells and the station's readings with the city's stats (`CityStats.sky`). Each client paints
    the radar from them pixel by pixel, so both clients show the same echoes.
  - Base reflectivity uses the NWS colour table, from 5 dBZ in steps of 5.
  - Base velocity is green toward the radar and red away, in knots. A thunderstorm turns about its core, so it shows a
    couplet.
  - There are four tilts, 0.5°, 0.9°, 1.3° and 1.8°, as at the bottom of a NEXRAD volume scan. The beam climbs with
    range by its tilt and the curve of the earth (a 4/3 earth). It sees less as it nears a cell's echo tops and
    nothing above them, so a high tilt overshoots shallow snow far off.
  - Each cell has a ragged edge and a mottled core from noise of its own seed.
  - The loop shows the last hour in six frames ten minutes apart.
- **Both clients** open the radar from the menu (Weather radar).
  - The scope has range rings every 10 km, the city's ground in gold and the radar in its middle.
  - It shows the frame's time on the day clock and a colour legend.
  - Beside it are the station's readings, the product, the tilt, the loop and the storm tool.
  - To set a storm going, tick "Draw on the radar" and drag from where the storm starts to where it'll be in an hour.
    The drag's length is its speed.

## Checks

- Sim: 4 new tests in `StormTests`:
  - over two days at each strength, the cell over the city agrees with the shower hours in every hour, and covers
    the whole hour;
  - a clear day has no cells, and a heatwave's thunderstorms never cross the city;
  - the station reads a cold clear January and a heavy shower (rain, humidity, lower pressure, since midnight
    adding up);
  - a placed storm 30 km west, heading east at 40 km/h, rains on the city and wets its roads, moves east, saves and
    loads, and is let go once it has passed;
  - a storm faster than 120 km/h is refused.

  All 308 sim tests pass.
- ClientModel: 4 new tests in `RadarTests`:
  - the beam's height, and a high tilt overshooting shallow snow;
  - the reflectivity and velocity colours, and the sign of velocity either side of the radar;
  - the server's shower cells painting rain over the city;
  - the loop carrying cells back along their tracks;
  - a drawn storm starting in the cell it was drawn from;
  - the station's lines.
- The Godot smoke run passes. The shot run sets three storms going round Toyville and photographs the radar
  (`view-18-radar.png`).
- Web `tsc` passes. Shots were taken with Playwright of both products.

## Known limits

- Each city's radar works out the front's cells round its own middle. Two neighbours' radars don't share the same
  cells yet; storms set going are only on the radar of the city that set them going.
- The radar has no map under it beyond the rings and the city's square.
- The loop carries the latest cells back along their tracks rather than showing past scans, so a placed storm
  appears before it was set going.
- No storm totals, echo tops or composite reflectivity yet. Those were the stretch goals.
