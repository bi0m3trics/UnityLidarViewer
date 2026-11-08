# LAS records are decoded in bounded chunks after rlas decompresses a LAZ to disk.
# Byte offsets and layouts follow LAS 1.0-1.4 point formats 0-10.
las_schema <- function(header) {
  format <- header[['Point Data Format ID']]
  lengths <- c(20, 28, 26, 34, 57, 63, 30, 36, 38, 59, 67)
  if (!format %in% 0:10) stop('Unsupported LAS point format.')
  fields <- list()
  add <- function(name, offset, size, kind = 'unsigned', shift = 0, mask = NULL, elements = 1) {
    fields[[length(fields) + 1L]] <<- list(name = name, offset = offset, size = size,
      kind = kind, shift = shift, mask = mask, elements = elements)
  }
  add('X', 0, 4, 'signed'); add('Y', 4, 4, 'signed'); add('Z', 8, 4, 'signed')
  add('intensity', 12, 2)
  modern <- format >= 6
  add('return_number', 14, 1, mask = if (modern) 15 else 7)
  add('number_of_returns', 14, 1, shift = if (modern) 4 else 3, mask = if (modern) 15 else 7)
  if (modern) {
    add('synthetic', 15, 1, mask = 1); add('key_point', 15, 1, shift = 1, mask = 1)
    add('withheld', 15, 1, shift = 2, mask = 1); add('overlap', 15, 1, shift = 3, mask = 1)
    add('scanner_channel', 15, 1, shift = 4, mask = 3)
    add('scan_direction_flag', 15, 1, shift = 6, mask = 1)
    add('edge_of_flight_line', 15, 1, shift = 7, mask = 1)
    add('classification', 16, 1); add('user_data', 17, 1)
    add('scan_angle', 18, 2, 'signed'); add('point_source_id', 20, 2)
    add('gps_time', 22, 8, 'float')
  } else {
    add('scan_direction_flag', 14, 1, shift = 6, mask = 1)
    add('edge_of_flight_line', 14, 1, shift = 7, mask = 1)
    add('classification', 15, 1, mask = 31)
    add('synthetic', 15, 1, shift = 5, mask = 1)
    add('key_point', 15, 1, shift = 6, mask = 1)
    add('withheld', 15, 1, shift = 7, mask = 1)
    add('scan_angle_rank', 16, 1, 'signed'); add('user_data', 17, 1)
    add('point_source_id', 18, 2)
    if (format %in% c(1, 3, 4, 5)) add('gps_time', 20, 8, 'float')
  }
  if (format %in% c(2, 3, 5, 7, 8, 10)) {
    rgb_offset <- if (format == 2) 20 else if (format %in% c(3, 5)) 28 else 30
    add('red', rgb_offset, 2); add('green', rgb_offset + 2, 2); add('blue', rgb_offset + 4, 2)
  }
  if (format %in% c(8, 10)) add('nir', 36, 2)
  if (format %in% c(4, 5, 9, 10)) {
    wave <- switch(as.character(format), '4' = 28, '5' = 34, '9' = 30, '10' = 38)
    add('wavepacket_index', wave, 1); add('wavepacket_offset', wave + 1, 8)
    add('wavepacket_size', wave + 9, 4); add('return_point_wave_location', wave + 13, 4, 'float')
    add('x_t', wave + 17, 4, 'float'); add('y_t', wave + 21, 4, 'float'); add('z_t', wave + 25, 4, 'float')
  }
  offset <- lengths[format + 1L]
  extras <- header[['Variable Length Records']][['Extra_Bytes']][['Extra Bytes Description']]
  for (extra in extras) {
    type <- extra$data_type
    if (type == 0) {
      offset <- offset + extra$options
      next
    }
    elements <- (type - 1L) %/% 10L + 1L
    base_type <- (type - 1L) %% 10L + 1L
    size <- c(1, 1, 2, 2, 4, 4, 8, 8, 4, 8)[base_type]
    kind <- if (base_type >= 9) 'float' else if (base_type %% 2 == 0) 'signed' else 'unsigned'
    if (extra$name %in% vapply(fields, `[[`, '', 'name')) stop('Duplicate LAS dimension name.')
    add(extra$name, offset, size, kind, elements = elements)
    fields[[length(fields)]]$scale <- extra$scale
    fields[[length(fields)]]$value_offset <- extra$offset
    offset <- offset + elements * size
  }
  if (offset > header[['Point Data Record Length']]) stop('Extra byte descriptors exceed the LAS record length.')
  fields
}

decode_las_field <- function(records, field) {
  bytes <- records[field$offset + seq_len(field$size * field$elements), , drop = FALSE]
  n <- ncol(records) * field$elements
  if (field$kind == 'float') {
    value <- readBin(as.raw(bytes), numeric(), n, size = field$size, endian = 'little')
  } else if (field$size == 1) {
    value <- as.integer(bytes)
    if (field$kind == 'signed') value[value >= 128] <- value[value >= 128] - 256
  } else if (field$size %in% c(2, 4)) {
    value <- as.double(readBin(as.raw(bytes), integer(), n, size = field$size,
      signed = field$kind == 'signed' || field$size == 4, endian = 'little'))
    # R reserves INT_MIN as NA, while LAS can legitimately store that bit pattern.
    if (field$size == 4) value[is.na(value)] <- -2147483648
    if (field$kind == 'unsigned' && field$size == 4) value[value < 0] <- value[value < 0] + 4294967296
  } else {
    octets <- matrix(as.integer(bytes), nrow = field$size)
    value <- as.numeric(crossprod(256 ^ (0:(field$size - 1)), octets))
    if (field$kind == 'signed') {
      negative <- octets[field$size, ] >= 128
      value[negative] <- value[negative] - 256 ^ field$size
    }
  }
  if (!is.null(field$mask)) value <- bitwAnd(bitwShiftR(as.integer(value), field$shift), field$mask)
  scaled <- !is.null(field$scale) || !is.null(field$value_offset)
  if (!is.null(field$scale)) value <- value * rep(field$scale, length.out = length(value))
  if (!is.null(field$value_offset)) value <- value + rep(field$value_offset, length.out = length(value))
  if (field$elements > 1) return(t(matrix(value, nrow = field$elements)))
  # Keep original integer bytes for exact hashing of 64-bit category IDs.
  if (field$size == 8 && field$kind != 'float' && !scaled) attr(value, 'integer_bytes') <- bytes
  attr(value, 'las_integer') <- field$kind != 'float' && !scaled
  value
}

read_las_chunks <- function(path, header, schema, callback, chunk_size = 250000L) {
  stream <- file(path, 'rb')
  on.exit(close(stream))
  seek(stream, header[['Offset to point data']], origin = 'start')
  total <- header[['Extended Number of point records']]
  if (is.null(total) || total == 0) total <- header[['Number of point records']]
  length <- header[['Point Data Record Length']]
  seen <- 0
  while (seen < total) {
    n <- min(chunk_size, total - seen)
    bytes <- readBin(stream, 'raw', n * length)
    if (base::length(bytes) != n * length) stop('Truncated LAS point data.')
    records <- matrix(bytes, nrow = length)
    points <- lapply(schema, function(field) decode_las_field(records, field))
    names(points) <- vapply(schema, `[[`, '', 'name')
    callback(points, seen, n)
    seen <- seen + n
  }
}

las_coordinates <- function(points, header) {
  cbind(points$X * header[['X scale factor']] + header[['X offset']],
    points$Y * header[['Y scale factor']] + header[['Y offset']],
    points$Z * header[['Z scale factor']] + header[['Z offset']])
}

categorical_values <- function(values, ids) {
  bytes <- attr(values, 'integer_bytes')
  missing <- !is.finite(values[ids])
  if (!is.null(bytes)) {
    bytes <- matrix(as.integer(bytes[, ids, drop = FALSE]), nrow = 8)
    limbs <- bytes[c(1, 3, 5, 7), , drop = FALSE] + 256 * bytes[c(2, 4, 6, 8), , drop = FALSE]
  } else if (isTRUE(attr(values, 'las_integer'))) {
    number <- values[ids]; number[missing] <- 0
    limbs <- vapply(0:3, function(i) floor(number / 65536 ^ i) %% 65536, numeric(length(ids)))
    limbs <- t(limbs)
  } else {
    number <- values[ids]
    bytes <- matrix(as.integer(writeBin(as.double(number), raw(), size = 8, endian = 'little')), nrow = 8)
    limbs <- bytes[c(1, 3, 5, 7), , drop = FALSE] + 256 * bytes[c(2, 4, 6, 8), , drop = FALSE]
  }
  # MurmurHash3's 64-bit finalizer prefix, using 16-bit limbs to avoid rounding.
  xor_shift <- function(parts) {
    parts[1, ] <- bitwXor(as.integer(parts[1, ]), as.integer(floor(parts[3, ] / 2) + (parts[4, ] %% 2) * 32768))
    parts[2, ] <- bitwXor(as.integer(parts[2, ]), as.integer(floor(parts[4, ] / 2)))
    parts
  }
  limbs <- xor_shift(limbs)
  product <- matrix(0, 4, length(ids))
  constant <- c(36045, 60757, 45015, 65361)
  carry <- rep(0, length(ids))
  for (i in 1:4) {
    value <- carry
    for (j in seq_len(i)) value <- value + limbs[j, ] * constant[i - j + 1L]
    product[i, ] <- value %% 65536
    carry <- floor(value / 65536)
  }
  product <- xor_shift(product)
  result <- (product[1, ] + (product[2, ] %% 256) * 65536) / 16777215
  result[missing] <- NA_real_
  result
}

write_mesh <- function(path, vertices, triangles) {
  stream <- file(path, 'wb'); on.exit(close(stream))
  writeBin(charToRaw('LM01'), stream)
  writeBin(as.integer(c(nrow(vertices), length(triangles))), stream, size = 4, endian = 'little')
  writeBin(as.double(t(vertices)), stream, size = 4, endian = 'little')
  writeBin(as.integer(t(triangles) - 1L), stream, size = 4, endian = 'little')
}

write_points <- function(path, local, rgba, intensity, scalar, classification, returns) {
  n <- nrow(local)
  records <- matrix(as.raw(0), 32, n)
  records[1:12, ] <- writeBin(as.double(t(local)), raw(), size = 4, endian = 'little')
  records[13:16, ] <- as.raw(t(rgba))
  records[17:20, ] <- writeBin(as.double(intensity), raw(), size = 4, endian = 'little')
  records[21:24, ] <- writeBin(as.double(scalar), raw(), size = 4, endian = 'little')
  records[25:28, ] <- writeBin(as.integer(classification), raw(), size = 4, endian = 'little')
  records[29:32, ] <- writeBin(as.integer(returns), raw(), size = 4, endian = 'little')
  stream <- file(path, 'wb'); on.exit(close(stream))
  writeBin(charToRaw('LP01'), stream)
  writeBin(as.integer(n), stream, size = 4, endian = 'little')
  writeBin(as.raw(records), stream)
}

# Preserve point order while combining small source chunks into GPU-sized batches.
pack_point_files <- function(out, files, target_points = 1000000L) {
  if (length(files) < 2) return(files)
  result <- character()
  output <- NULL
  count <- 0L
  on.exit(if (!is.null(output)) close(output))
  finish <- function() {
    seek(output, 4, origin = 'start', rw = 'write')
    writeBin(as.integer(count), output, size = 4, endian = 'little')
    close(output)
    output <<- NULL
  }
  for (filename in files) {
    input <- file(file.path(out, filename), 'rb')
    bytes <- tryCatch({
      if (rawToChar(readBin(input, 'raw', 4)) != 'LP01') stop('Invalid point file while packing.')
      n <- readBin(input, integer(), 1, size = 4, endian = 'little')
      records <- readBin(input, 'raw', n * 32)
      if (length(records) != n * 32) stop('Truncated point file while packing.')
      list(count = n, records = records)
    }, finally = close(input))
    if (!is.null(output) && count + bytes$count > target_points) finish()
    if (is.null(output)) {
      name <- sprintf('packed_points_%04d.bytes', length(result))
      result <- c(result, name)
      output <- file(file.path(out, name), 'wb')
      writeBin(charToRaw('LP01'), output)
      writeBin(0L, output, size = 4, endian = 'little')
      count <- 0L
    }
    writeBin(bytes$records, output)
    count <- count + bytes$count
  }
  if (!is.null(output)) finish()
  # Inputs are removed only after every replacement batch has been completed.
  unlink(file.path(out, files))
  result
}
