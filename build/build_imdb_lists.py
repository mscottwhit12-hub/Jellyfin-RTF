"""Builds the ready-made "Top 10 movies of a year" lists from IMDb's official data files.

IMDb publishes its ratings as free files for personal, non-commercial use:
https://developer.imdb.com/non-commercial-datasets/ . This script reads two of them and writes the lists the
plugin ships with, so the plugin needs no API key and no internet access to show them.

How a list is made (keep in step with TopTenService.DefinitionText):
- Feature films (IMDb type "movie") first released that year. TV movies, specials, shorts and videos are left out.
- At least 100,000 IMDb votes, so the list is films people have actually heard of and a handful of
  enthusiastic voters can't push an obscure film in. If a year doesn't have 10 such films the minimum
  steps down to 50,000, then 25,000 (IMDb's own Top 250 minimum).
- No documentaries (this also keeps concert films out), nothing under an hour, nothing marked adult.
- Sorted by IMDb rating, best first. Ties go to the film with more votes, then by title.

Usage:
    python build_imdb_lists.py --basics title.basics.tsv.gz --ratings title.ratings.tsv.gz --out lists.json
"""
import argparse
import csv
import datetime
import gzip
import json
import sys

YEARS = [2020, 2021, 2022, 2023, 2024, 2025]
VOTE_STEPS = [100_000, 50_000, 25_000]
LIST_SIZE = 10
MIN_RUNTIME = 60
ATTRIBUTION = "Information courtesy of IMDb (https://www.imdb.com). Used with permission."

csv.field_size_limit(sys.maxsize)


def rows(path):
    """Yields each row of an IMDb .tsv.gz file as a dict. IMDb writes \\N for empty."""
    with gzip.open(path, "rt", encoding="utf-8", newline="") as f:
        reader = csv.reader(f, delimiter="\t", quoting=csv.QUOTE_NONE)
        header = next(reader)
        for values in reader:
            if len(values) != len(header):
                continue
            yield {k: (None if v == "\\N" else v) for k, v in zip(header, values)}


def build(basics_path, ratings_path):
    floor = VOTE_STEPS[-1]
    ratings = {}
    for r in rows(ratings_path):
        try:
            votes = int(r["numVotes"])
        except (TypeError, ValueError):
            continue
        if votes >= floor:
            ratings[r["tconst"]] = (float(r["averageRating"]), votes)

    candidates = {year: [] for year in YEARS}
    for b in rows(basics_path):
        if b["titleType"] != "movie" or b["isAdult"] == "1" or b["tconst"] not in ratings:
            continue
        try:
            year = int(b["startYear"])
            runtime = int(b["runtimeMinutes"])
        except (TypeError, ValueError):
            continue
        if year not in candidates or runtime < MIN_RUNTIME:
            continue
        genres = (b["genres"] or "").split(",")
        if "Documentary" in genres:
            continue
        rating, votes = ratings[b["tconst"]]
        candidates[year].append({
            "imdbId": b["tconst"],
            "title": b["primaryTitle"],
            "originalTitle": b["originalTitle"] if b["originalTitle"] != b["primaryTitle"] else None,
            "year": year,
            "runtimeMinutes": runtime,
            "genres": [g for g in genres if g],
            "rating": rating,
            "votes": votes,
        })

    years = {}
    for year in YEARS:
        ordered = sorted(candidates[year], key=lambda m: (-m["rating"], -m["votes"], m["title"].casefold()))
        for minimum in VOTE_STEPS:
            chosen = [m for m in ordered if m["votes"] >= minimum][:LIST_SIZE]
            if len(chosen) == LIST_SIZE:
                break
        if len(chosen) < LIST_SIZE:
            raise SystemExit(f"{year}: only {len(chosen)} films qualify; refusing to write a short list")
        years[str(year)] = {
            "minimumVotes": minimum,
            "movies": [dict(rank=i + 1, **m) for i, m in enumerate(chosen)],
        }

    return {
        "source": "IMDb",
        "generatedUtc": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "attribution": ATTRIBUTION,
        "years": years,
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--basics", required=True)
    parser.add_argument("--ratings", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args()
    data = build(args.basics, args.ratings)
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
        f.write("\n")
    for year, entry in data["years"].items():
        print(f"{year} (min {entry['minimumVotes']:,} votes)")
        for m in entry["movies"]:
            print(f"  {m['rank']:>2}. {m['title']} ({m['rating']}, {m['votes']:,} votes) {m['imdbId']}")


if __name__ == "__main__":
    main()
