#!/usr/bin/env python3
"""
Generates import documents shaped like real Japanese export stock.

Committed rather than run once and forgotten, so the fixtures can be regenerated and the
choices behind them can be argued with. Deterministic by default - the same seed produces the
same catalogue, which is what makes a POC measurement repeatable.

The realism that matters here is not the model names. It is that:

  * Most records carry a chassis number and NO VIN. Japanese domestic-market cars are not
    VIN-issued the way export-market cars are, so a catalogue that gives every vehicle a tidy
    VIN would make deduplication look far better than it will ever be in practice.
  * Some records carry neither, which is the case dedup genuinely cannot help with.
  * A minority state an incoterm. Most exporters publish a price without saying FOB or CIF,
    and the difference is the whole cost of shipping.
  * lastSeenAtUtc is spread over weeks, because a catalogue where everything was confirmed
    today cannot exercise a staleness warning.

Usage:  python3 tools/generate-import-data.py [--count 400] [--seed 42] [--out DIR]
"""

import argparse
import json
import random
from datetime import datetime, timedelta, timezone

# (make, model, variants, body, cc, fuel, transmission, drivetrain, typical price band in JPY)
CATALOGUE = [
    ("Toyota", "Hiace Van", ["DX GL Package", "Super GL", "DX Long"],
     "Van", 2982, "diesel", "automatic", "rwd", (900_000, 2_600_000)),
    ("Toyota", "Land Cruiser Prado", ["TX", "TX L Package", "TZ-G"],
     "SUV", 2693, "petrol", "automatic", "4wd", (1_800_000, 4_500_000)),
    ("Toyota", "Corolla Axio", ["X", "G", "Hybrid G"],
     "Sedan", 1496, "petrol", "cvt", "fwd", (450_000, 1_300_000)),
    ("Toyota", "Aqua", ["S", "G", "L"],
     "Hatchback", 1496, "hybrid", "cvt", "fwd", (400_000, 1_200_000)),
    ("Toyota", "Vitz", ["F", "Jewela", "RS"],
     "Hatchback", 1329, "petrol", "cvt", "fwd", (300_000, 900_000)),
    ("Nissan", "X-Trail", ["20X", "20S", "Hybrid 20X"],
     "SUV", 1997, "petrol", "cvt", "4wd", (700_000, 2_200_000)),
    ("Nissan", "Note", ["X", "Medalist", "e-Power X"],
     "Hatchback", 1198, "hybrid", "cvt", "fwd", (350_000, 1_100_000)),
    ("Nissan", "Caravan", ["DX", "Premium GX"],
     "Van", 2488, "diesel", "automatic", "rwd", (800_000, 2_400_000)),
    ("Honda", "Fit", ["G", "Hybrid", "RS"],
     "Hatchback", 1317, "petrol", "cvt", "fwd", (350_000, 1_000_000)),
    ("Honda", "Vezel", ["G", "Hybrid Z", "RS"],
     "SUV", 1496, "hybrid", "dct", "fwd", (900_000, 2_300_000)),
    ("Honda", "Freed", ["G", "Hybrid G", "Crosstar"],
     "Minivan", 1496, "hybrid", "cvt", "fwd", (700_000, 1_900_000)),
    ("Mitsubishi", "Pajero", ["Exceed", "Super Exceed"],
     "SUV", 2972, "diesel", "automatic", "4wd", (700_000, 2_100_000)),
    ("Mitsubishi", "Canter", ["Standard", "Wide Body"],
     "Truck", 2998, "diesel", "manual", "rwd", (900_000, 2_800_000)),
    ("Suzuki", "Every", ["Join", "PC Limited"],
     "Van", 658, "petrol", "automatic", "rwd", (300_000, 900_000)),
    ("Suzuki", "Jimny", ["XC", "XL", "Land Venture"],
     "SUV", 658, "petrol", "manual", "4wd", (600_000, 2_000_000)),
]

COLORS = ["White", "Pearl White", "Silver", "Black", "Gunmetal", "Blue", "Red", "Beige"]
PORTS = ["Yokohama", "Nagoya", "Kobe", "Osaka", "Moji", "Hakata"]
MARKETS = [["PK"], ["KE", "TZ", "UG"], ["PK", "AE"], ["ZM", "MW"], ["LK"], ["KE"], []]

# Chassis prefixes that actually correspond to the models above, because a chassis number is
# the primary identifier for most of this stock and a random string would defeat the point of
# testing deduplication against it.
CHASSIS_PREFIX = {
    "Hiace Van": "KDH201", "Land Cruiser Prado": "TRJ150", "Corolla Axio": "NZE161",
    "Aqua": "NHP10", "Vitz": "KSP130", "X-Trail": "NT32", "Note": "E12",
    "Caravan": "VR2E26", "Fit": "GK3", "Vezel": "RU1", "Freed": "GB5",
    "Pajero": "V98W", "Canter": "FEB50", "Every": "DA17V", "Jimny": "JB64W",
}


def build(rng: random.Random, index: int, source: str, now: datetime) -> dict:
    make, model, variants, body, cc, fuel, gearbox, drive, (lo, hi) = rng.choice(CATALOGUE)

    year = rng.randint(2008, 2023)

    # Older cars have run further. A flat mileage distribution would make year and mileage
    # independent, which no real catalogue is.
    age = max(1, 2026 - year)
    mileage = min(320_000, max(5_000, int(rng.gauss(age * 12_000, age * 3_500))))

    # Price falls with age and distance. Rough, but it means price sorting and range filters
    # are exercised against a plausible distribution rather than uniform noise.
    depreciation = max(0.15, 1.0 - (age * 0.07) - (mileage / 400_000))
    price = int(round(rng.uniform(lo, hi) * depreciation, -4))

    record = {
        "externalId": f"{source.upper()[:3]}-{100000 + index}",
        "listingUrl": f"https://example-exporter.test/{source}/{100000 + index}",
        "make": make,
        "model": model,
        "variant": rng.choice(variants),
        "year": year,
        "mileage": mileage,
        "mileageUnit": "km",
        "steering": "rhd" if rng.random() < 0.97 else "lhd",
        "fuelType": fuel,
        "transmission": gearbox,
        "drivetrain": drive,
        "bodyType": body,
        "engineCc": cc,
        "exteriorColor": rng.choice(COLORS),
        "price": price,
        "currency": "JPY",
        "locationCountry": "JP",
        "portOfLoading": rng.choice(PORTS),
        "destinationMarkets": rng.choice(MARKETS),
        "imageUrls": [
            f"https://picsum.photos/seed/{source}{index}-{n}/800/600" for n in range(rng.randint(1, 5))
        ],
    }

    # Incoterm: stated on a minority, exactly as in the real trade. An absent one must stay
    # absent so the Unknown tag is exercised rather than assumed away.
    roll = rng.random()
    if roll < 0.35:
        record["priceType"] = "FOB"
    elif roll < 0.45:
        record["priceType"] = "CIF"

    # Identity. Chassis on most, VIN on a few, neither on some - the distribution that decides
    # how much deduplication can ever achieve here.
    roll = rng.random()
    if roll < 0.75:
        prefix = CHASSIS_PREFIX.get(model, "XXX000")
        record["chassisNumber"] = f"{prefix}-{rng.randint(1000000, 9999999)}"
    elif roll < 0.88:
        record["vin"] = "JT" + "".join(rng.choice("ABCDEFGHJKLMNPRSTUVWXYZ0123456789") for _ in range(15))
    # else: neither. Nothing to merge on, and the import reports it.

    # Freshness, spread over six weeks so the staleness warning has something to warn about.
    seen = now - timedelta(days=rng.triangular(0, 42, 3), hours=rng.uniform(0, 23))
    record["lastSeenAtUtc"] = seen.replace(microsecond=0).isoformat().replace("+00:00", "Z")
    record["firstSeenAtUtc"] = (seen - timedelta(days=rng.randint(1, 120))) \
        .replace(microsecond=0).isoformat().replace("+00:00", "Z")

    # Availability: mostly true, some explicitly withdrawn, some unstated. All three paths
    # matter - unstated must stay searchable, false must not.
    roll = rng.random()
    if roll < 0.85:
        record["isAvailable"] = True
    elif roll < 0.93:
        record["isAvailable"] = False
    # else: absent, meaning the source did not say.

    return record


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--count", type=int, default=400)
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--out", default="docs/spec/examples")
    args = parser.parse_args()

    rng = random.Random(args.seed)
    now = datetime.now(timezone.utc).replace(microsecond=0)

    primary_n = int(args.count * 0.6)
    primary = [build(rng, i, "exporter-a", now) for i in range(primary_n)]
    secondary = [build(rng, i, "exporter-b", now) for i in range(args.count - primary_n)]

    # Deliberate overlap: the same physical cars offered by both exporters, which is exactly
    # what happens in this trade and the only way auto-merge has anything to find. Only records
    # that carry an identifier are duplicated - copying an unidentifiable one would produce two
    # rows that SHOULD stay separate, which is a different test.
    identifiable = [v for v in primary if "chassisNumber" in v or "vin" in v]
    overlap = rng.sample(identifiable, min(25, len(identifiable)))

    for original in overlap:
        clone = dict(original)
        clone["externalId"] = original["externalId"].replace("EXP-", "EXB-", 1) + "-x"
        clone["listingUrl"] = original["listingUrl"].replace("exporter-a", "exporter-b")

        # Same car, different offer: the other exporter prices it differently and saw it at a
        # different time. Identity is the chassis or VIN, never the price.
        clone["price"] = int(round(original["price"] * rng.uniform(0.92, 1.12), -4))
        clone["imageUrls"] = original["imageUrls"][:1]
        secondary.append(clone)

    rng.shuffle(secondary)

    for name, vehicles in (("exporter-a", primary), ("exporter-b", secondary)):
        document = {
            "sourceCode": name,
            "capturedAtUtc": now.isoformat().replace("+00:00", "Z"),
            "vehicles": vehicles,
        }
        path = f"{args.out}/import-{name}.json"
        with open(path, "w", encoding="utf-8") as handle:
            json.dump(document, handle, indent=2)
            handle.write("\n")

        with_vin = sum(1 for v in vehicles if "vin" in v)
        with_chassis = sum(1 for v in vehicles if "chassisNumber" in v)
        neither = len(vehicles) - with_vin - with_chassis
        with_incoterm = sum(1 for v in vehicles if "priceType" in v)

        print(f"{path}: {len(vehicles)} vehicles")
        print(f"    vin {with_vin} | chassis {with_chassis} | neither {neither}"
              f" | incoterm stated {with_incoterm}")

    print(f"\n{len(overlap)} vehicles appear in both files, sharing an identifier.")
    print("Importing exporter-a then exporter-b should report that many as autoMerged.")


if __name__ == "__main__":
    main()
