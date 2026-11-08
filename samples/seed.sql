-- Sample data for PostGISApi (Swiss examples, mixed SRIDs).
-- Usage: psql -d postgisapi_test -f samples/seed.sql
CREATE EXTENSION IF NOT EXISTS postgis;

DROP TABLE IF EXISTS public.staedte, public.gemeinden, public.poi, public.routen CASCADE;

-- Points in WGS84
CREATE TABLE public.staedte (
    id          integer PRIMARY KEY,
    name        text NOT NULL,
    kanton      char(2) NOT NULL,
    einwohner   integer,
    hauptort    boolean DEFAULT false,
    geom        geometry(Point, 4326)
);
INSERT INTO public.staedte VALUES
    (1, 'Zürich',     'ZH', 421878, true,  ST_SetSRID(ST_MakePoint(8.5417, 47.3769), 4326)),
    (2, 'Genève',     'GE', 203856, true,  ST_SetSRID(ST_MakePoint(6.1432, 46.2044), 4326)),
    (3, 'Basel',      'BS', 173863, true,  ST_SetSRID(ST_MakePoint(7.5886, 47.5596), 4326)),
    (4, 'Lausanne',   'VD', 139111, true,  ST_SetSRID(ST_MakePoint(6.6323, 46.5197), 4326)),
    (5, 'Bern',       'BE', 134591, true,  ST_SetSRID(ST_MakePoint(7.4474, 46.9480), 4326)),
    (6, 'Winterthur', 'ZH', 114220, false, ST_SetSRID(ST_MakePoint(8.7241, 47.4988), 4326)),
    (7, 'Luzern',     'LU',  82257, true,  ST_SetSRID(ST_MakePoint(8.3093, 47.0502), 4326)),
    (8, 'St. Gallen', 'SG',  75833, true,  ST_SetSRID(ST_MakePoint(9.3767, 47.4245), 4326)),
    (9, 'Lugano',     'TI',  62315, false, ST_SetSRID(ST_MakePoint(8.9511, 46.0037), 4326)),
    (10,'Biel/Bienne','BE',  55159, false, ST_SetSRID(ST_MakePoint(7.2467, 47.1368), 4326)),
    (11,'Thun',       'BE',  43734, false, ST_SetSRID(ST_MakePoint(7.6280, 46.7580), 4326)),
    (12,'Chur',       'GR',  37500, true,  ST_SetSRID(ST_MakePoint(9.5320, 46.8499), 4326));
CREATE INDEX ON public.staedte USING gist (geom);

-- Polygons in Swiss LV95 (EPSG:2056): simplified municipality areas
CREATE TABLE public.gemeinden (
    bfs_nr      integer PRIMARY KEY,
    name        text NOT NULL,
    flaeche_km2 numeric(10, 2),
    geom        geometry(MultiPolygon, 2056)
);
INSERT INTO public.gemeinden (bfs_nr, name, geom)
SELECT 1000 + id, name, ST_Multi(ST_Buffer(ST_Transform(geom, 2056), 3000 + einwohner / 100.0, 8))
FROM public.staedte;
UPDATE public.gemeinden SET flaeche_km2 = round((ST_Area(geom) / 1e6)::numeric, 2);
CREATE INDEX ON public.gemeinden USING gist (geom);

-- Writable layer
CREATE TABLE public.poi (
    id          serial PRIMARY KEY,
    name        text NOT NULL,
    kategorie   text,
    tags        text[],
    attribute   jsonb,
    erstellt    timestamptz DEFAULT now(),
    geom        geometry(Point, 4326)
);
INSERT INTO public.poi (name, kategorie, tags, geom) VALUES
    ('Bundeshaus', 'Politik', ARRAY['bern', 'sehenswuerdigkeit'], ST_SetSRID(ST_MakePoint(7.4440, 46.9466), 4326)),
    ('Kapellbrücke', 'Sehenswürdigkeit', ARRAY['luzern'], ST_SetSRID(ST_MakePoint(8.3077, 47.0516), 4326));
CREATE INDEX ON public.poi USING gist (geom);

-- Lines
CREATE TABLE public.routen (
    id    serial PRIMARY KEY,
    name  text,
    geom  geometry(LineString, 4326)
);
INSERT INTO public.routen (name, geom)
SELECT a.name || ' - ' || b.name, ST_MakeLine(a.geom, b.geom)
FROM public.staedte a JOIN public.staedte b ON (a.id, b.id) IN ((1, 5), (5, 2), (1, 3), (5, 7));
CREATE INDEX ON public.routen USING gist (geom);
