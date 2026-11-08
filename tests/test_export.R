# Run from the project root with Rscript tests/test_export.R.
source('export_to_unity.R')

run_tests <- function() {
  root <- tempfile('r-export-test-')
  dir.create(root)
  on.exit(unlink(root, recursive = TRUE))
  points <- data.table(X = c(1001, 1002, 1003, 1003.1, 1003.2, 1003.3, 1003.4, 1004),
    Y = c(2001, 2002, 2003, 2003.1, 2003.2, 2003.3, 2003.4, 2004),
    Z = c(10, 10, 12, 12, 12, 12, 12, 15), Intensity = as.integer(100 * 1:8),
    Classification = as.integer(c(2, 2, 0, 0, 0, 0, 0, 7)), ReturnNumber = 1L, NumberOfReturns = 1L,
    R = 65535L, G = 0L, B = 0L, TreeID = c(0, 0, 16777216, 0, 16777217, 0, 16777216, 0))
  header <- rlas::header_add_extrabytes_manual(rlas::header_create(points), 'TreeID', 'Tree identifiers', 5)
  laz <- file.path(root, 'test.laz'); rlas::write.las(laz, header, points)
  dem <- terra::rast(nrows = 8, ncols = 8, xmin = 1000, xmax = 1008, ymin = 2000, ymax = 2008, crs = '')
  terra::values(dem) <- c(NA, rep(10, 63))
  tif <- file.path(root, 'test.tif'); terra::writeRaster(dem, tif)
  out <- file.path(root, 'scene')
  args <- parse_options(c(laz, tif, '--out', out, '--max-points', '4', '--dem-stride', '1', '--min-voxel-points', '1', '--ply'))
  export_scene(args)
  manifest <- jsonlite::read_json(file.path(out, 'manifest.json'), simplifyVector = TRUE)
  stopifnot(manifest$displayPointCount == 4, manifest$collisionVoxelCount > 0)
  stream <- file(file.path(out, manifest$pointFiles[1]), 'rb')
  raw <- readBin(stream, 'raw', 8 + 4 * 32); close(stream)
  stopifnot(rawToChar(raw[1:4]) == 'LP01')
  records <- matrix(raw[-(1:8)], nrow = 32)
  batch_folder <- file.path(root, 'batches'); dir.create(batch_folder)
  for (name in c('a.bytes', 'b.bytes')) file.copy(file.path(out, manifest$pointFiles[1]), file.path(batch_folder, name))
  packed <- pack_point_files(batch_folder, c('a.bytes', 'b.bytes'))
  stream <- file(file.path(batch_folder, packed[1]), 'rb')
  packed_raw <- readBin(stream, 'raw', 8 + 8 * 32); close(stream)
  stopifnot(length(packed) == 1, readBin(packed_raw[5:8], integer(), 1, size = 4, endian = 'little') == 8L,
    identical(packed_raw[-(1:8)], rep(raw[-(1:8)], 2)), !file.exists(file.path(batch_folder, 'a.bytes')))
  xyz <- matrix(readBin(as.raw(records[1:12, ]), numeric(), 12, size = 4, endian = 'little'), ncol = 3, byrow = TRUE)
  intensity <- readBin(as.raw(records[17:20, ]), numeric(), 4, size = 4, endian = 'little')
  stopifnot(max(abs(xyz[1, ] - c(-1.5, 0, -1.5))) < 1e-6,
    identical(intensity, c(100, 300, 500, 700)), identical(as.integer(records[13:16, 1]), c(255L, 0L, 0L, 255L)))
  read_field <- function(name) {
    filename <- manifest$displayFields$file[manifest$displayFields$name == name]
    stream <- file(file.path(out, filename), 'rb'); on.exit(close(stream))
    readBin(stream, numeric(), 4, size = 4, endian = 'little')
  }
  ids <- read_field('TreeID')
  stopifnot(ids[2] == ids[4], ids[2] != ids[3], identical(read_field('HAG (from DEM)'), c(0, 1, 1, 1)))
  # Spatial pages preserve field alignment while shuffling every row together.
  tiled_out <- file.path(root, 'tiled')
  tiled <- tile_scene(out, tiled_out, tile_size = 1, page_points = 2)
  stopifnot(tiled$version == 2, sum(vapply(tiled$tiles, `[[`, 0, 'count')) == 4,
    length(tiled$displayFields) == nrow(manifest$displayFields))
  original_keys <- apply(records[1:12, , drop = FALSE], 2, function(bytes) paste(as.integer(bytes), collapse = ','))
  observed <- integer()
  for (tile in tiled$tiles) {
    stream <- file(file.path(tiled_out, tile$file), 'rb')
    magic <- rawToChar(readBin(stream, 'raw', 4))
    header <- readBin(stream, integer(), 2, size = 4, endian = 'little')
    compact <- matrix(readBin(stream, 'raw', header[1] * 16), nrow = 16)
    values <- matrix(readBin(stream, numeric(), header[1] * header[2], size = 4, endian = 'little'), nrow = header[1])
    close(stream)
    keys <- apply(compact[1:12, , drop = FALSE], 2, function(bytes) paste(as.integer(bytes), collapse = ','))
    index <- match(keys, original_keys)
    observed <- c(observed, index)
    stopifnot(magic == 'LT01', header[1] <= 2, !anyNA(index))
    for (field in seq_len(header[2])) stopifnot(identical(values[, field], read_field(manifest$displayFields$name[field])[index]))
    stopifnot(all(as.integer(compact[16, index == 1]) == 0))
  }
  stopifnot(identical(sort(observed), 1:4))
  streaming_args <- args
  streaming_args$out <- file.path(root, 'streaming-cli')
  streaming_args$streaming <- TRUE
  streamed <- export_scene(streaming_args)
  stopifnot(streamed$version == 2, file.exists(file.path(streaming_args$out, 'cloud.ply')))
  stopifnot(inherits(try(export_scene(args), silent = TRUE), 'try-error'))
  modern <- copy(points)
  modern[, `:=`(Classification = 200L, Overlap_flag = TRUE, ScannerChannel = 2L)]
  modern_header <- rlas::header_add_extrabytes_manual(rlas::header_create(modern), 'TreeID', 'Tree identifiers', 5)
  modern_las <- file.path(root, 'modern.las')
  rlas::write.las(modern_las, modern_header, modern)
  modern_header <- rlas::read.lasheader(modern_las)
  stopifnot(modern_header[['Point Data Format ID']] >= 6)
  seen_points <- 0L
  read_las_chunks(modern_las, modern_header, las_schema(modern_header), function(chunk, seen, n) {
    stopifnot(all(chunk$classification == 200), all(chunk$overlap == 1), all(chunk$scanner_channel == 2),
      all(chunk$return_number == 1), max(abs(las_coordinates(chunk, modern_header)[, 1] - points$X[seen + seq_len(n)])) < 1e-6)
    seen_points <<- seen_points + n
  }, chunk_size = 3L)
  stopifnot(seen_points == 8)
  mesh <- dem_mesh(dem, as.numeric(terra::values(dem)), c(1000, 2000, 10), 1, 1)
  stopifnot(nrow(mesh$vertices) == 63, nrow(mesh$triangles) == 97)
  a <- mesh$vertices[mesh$triangles[, 2], ] - mesh$vertices[mesh$triangles[, 1], ]
  b <- mesh$vertices[mesh$triangles[, 3], ] - mesh$vertices[mesh$triangles[, 1], ]
  stopifnot(all(a[, 3] * b[, 1] - a[, 1] * b[, 3] > 0))

  # Neighboring voxels share no internal faces.
  counts <- data.table(x = c(0, 1, 3), y = 0, z = 0, count = c(5, 5, 1))
  collider_folder <- file.path(root, 'collisions'); dir.create(collider_folder)
  collision <- collision_meshes(counts, 5, .5, collider_folder)
  stream <- file(file.path(collider_folder, collision$files[1]), 'rb')
  magic <- readBin(stream, 'raw', 4); sizes <- readBin(stream, integer(), 2, size = 4, endian = 'little'); close(stream)
  stopifnot(collision$count == 2, identical(sizes, c(40L, 60L)))
  distant <- data.table(x = c(0, 32), y = 0, z = 0, count = 5)
  tiles_folder <- file.path(root, 'tiles'); dir.create(tiles_folder)
  tiles <- collision_meshes(distant, 5, .5, tiles_folder)
  stopifnot(length(tiles$files) == 2)
  for (filename in tiles$files) {
    stream <- file(file.path(tiles_folder, filename), 'rb')
    readBin(stream, 'raw', 4)
    sizes <- readBin(stream, integer(), 2, size = 4, endian = 'little'); close(stream)
    stopifnot(identical(sizes, c(24L, 36L)))
  }

  # IDs differing below double precision retain distinct colors from original bytes.
  bytes <- matrix(as.raw(0), nrow = 8, ncol = 3)
  bytes[7, ] <- as.raw(32); bytes[1, 2] <- as.raw(1)
  values <- decode_las_field(bytes, list(offset = 0, size = 8, elements = 1, kind = 'unsigned', mask = NULL))
  hashed <- categorical_values(values, 1:3)
  stopifnot(values[1] == values[2], hashed[1] != hashed[2], hashed[1] == hashed[3])

  # Elevated returns cannot pull the generated ground up to a roof-like patch.
  xy <- expand.grid(x = seq(.25, 23.75, .5), y = seq(.25, 23.75, .5))
  raised <- xy$x > 10 & xy$x < 13 & xy$y > 10 & xy$y < 13
  cloud <- data.table(X = xy$x, Y = xy$y, Z = 10 + .02 * xy$x + 6 * raised, Intensity = 1L,
    Classification = 0L, ReturnNumber = 1L, NumberOfReturns = 1L)
  unclassified <- file.path(root, 'unclassified.laz')
  rlas::write.las(unclassified, rlas::header_create(cloud), cloud)
  ptd_out <- file.path(root, 'ptd')
  export_scene(parse_options(c(unclassified, '--out', ptd_out)))
  ground <- terra::rast(file.path(ptd_out, 'generated-dem.tif'), guessCRS = FALSE)
  estimate <- terra::extract(ground, matrix(c(11.25, 11.25), nrow = 1))[1, 1]
  stopifnot(abs(estimate - 10.225) < .12)

  # A persistent vertical stem gets a compact proxy, horizontal foliage does not.
  angle <- rep(seq(0, 2 * pi, length.out = 65)[-65], 4)
  heights <- rep(c(.4, .8, 1.15, 1.55), each = 64)
  local <- cbind(2 + .18 * cos(angle), heights, 3 + .18 * sin(angle))
  columns <- collect_columns(local, heights, rep(TRUE, length(heights)))
  fitted <- write_slim_scene(root, columns, function(x, y) rep(10, length(x)), c(0, 0, 10), 1)
  proxy <- jsonlite::read_json(file.path(root, fitted$file), simplifyVector = TRUE)$colliders
  stopifnot(fitted$count == 1, abs(proxy$x - 2) < .05, abs(proxy$z - 3) < .05,
    proxy$radius > .10, proxy$radius < .23)
  cat('R_EXPORT_TESTS_PASS point format, RGB, HAG, exact category IDs, mesh holes and winding, collisions, PTD, output protection\n')
}

run_tests()
