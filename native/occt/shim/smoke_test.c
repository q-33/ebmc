/*
 * Copyright (c) 2026, East Bay Makers Club and contributors
 * SPDX-License-Identifier: BSD-2-Clause
 *
 * Redistribution and use in source and binary forms, with or without modification, are permitted provided that the
 * following conditions are met:
 * 1. Redistributions of source code must retain the above copyright notice, this list of conditions and the following
 *    disclaimer.
 * 2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following
 *    disclaimer in the documentation and/or other materials provided with the distribution.
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
 * INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
 * SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
 * WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 *
 * This file is EBMC's own wrapper code. The library built from it (libebmc_occt) also contains Open CASCADE
 * Technology, which is under the GNU LGPL 2.1 with the Open CASCADE exception; that library as a whole is
 * distributed under those terms (see native/occt/README.md and StaticData/License/OpenCASCADE.txt).
 */
/* Native smoke test for libebmc_occt: box, fillet, cut, tessellate, STEP round trip, error path. */
#include "ebmc_occt.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>

static int failures = 0;

#define CHECK(cond, ...)                                       \
    do {                                                       \
        if (cond) { printf("ok    "); }                        \
        else { printf("FAIL  "); failures++; }                 \
        printf(__VA_ARGS__);                                   \
        printf("\n");                                          \
    } while (0)

#define CALL(expr)                                                              \
    do {                                                                        \
        int rc_ = (expr);                                                       \
        if (rc_ != EBMC_OK) {                                                   \
            printf("FAIL  %s -> %d: %s\n", #expr, rc_, ebmc_occt_last_error()); \
            return 1;                                                           \
        }                                                                       \
    } while (0)

int main(void)
{
    printf("%s\n", ebmc_occt_version());

    ebmc_shape *box = NULL, *fil = NULL, *cyl = NULL, *cylm = NULL, *cut = NULL, *rd = NULL, *bad = NULL;
    double v = 0;
    int32_t f, e, n;

    CALL(ebmc_make_box(20, 20, 20, &box));
    CALL(ebmc_volume(box, &v));
    CALL(ebmc_shape_counts(box, &f, &e, &n));
    CHECK(fabs(v - 8000) < 1e-6 && f == 6 && e == 12 && n == 8, "box volume %.9f, %d faces %d edges %d vertices", v, f, e, n);

    int32_t edge0 = 0;
    double a[3], b[3], len;
    CALL(ebmc_edge_info(box, edge0, a, b, &len, NULL, NULL));
    printf("      edge 0: (%g,%g,%g)-(%g,%g,%g) length %g\n", a[0], a[1], a[2], b[0], b[1], b[2], len);

    CALL(ebmc_fillet_edges(box, &edge0, 1, 3.0, &fil));
    CALL(ebmc_volume(fil, &v));
    CALL(ebmc_shape_counts(fil, &f, NULL, NULL));
    double expect = 8000 - (1 - M_PI / 4) * 9 * 20;
    CHECK(fabs(v - expect) / expect < 1e-6 && f == 7, "fillet volume %.9f (expected %.9f), %d faces", v, expect, f);

    CALL(ebmc_make_cylinder(2.5, 40, &cyl));
    CALL(ebmc_translate(cyl, 10, 10, -10, &cylm));
    CALL(ebmc_boolean(fil, cylm, EBMC_BOOL_CUT, &cut));
    CALL(ebmc_volume(cut, &v));
    expect = 8000 - (1 - M_PI / 4) * 9 * 20 - M_PI * 2.5 * 2.5 * 20;
    CHECK(fabs(v - expect) / expect < 1e-6, "cut volume %.9f (expected %.9f)", v, expect);

    ebmc_mesh* m = NULL;
    CALL(ebmc_tessellate(cut, 0.05, 0.5 * M_PI / 180, &m));
    CHECK(m->triangle_count > 0, "mesh: %d vertices, %d triangles, %d edge polylines", m->vertex_count, m->triangle_count, m->edge_count);
    ebmc_mesh_free(m);

    CALL(ebmc_write_step(cut, "smoke_test.step"));
    CALL(ebmc_read_step("smoke_test.step", &rd));
    double v2 = 0;
    CALL(ebmc_volume(rd, &v2));
    CHECK(fabs(v2 - v) / v < 1e-6, "STEP round trip volume %.9f", v2);

    /* 0.2: edge geometry, general transform, compound */
    {
        int32_t ct = -1; double mid[3], tan[3], rad = -1;
        CALL(ebmc_edge_geom(box, 0, &ct, mid, tan, &rad));
        CHECK(ct == EBMC_CURVE_LINE && rad == 0, "edge 0 geom: type %d mid (%g,%g,%g) tangent (%g,%g,%g)", ct, mid[0], mid[1], mid[2], tan[0], tan[1], tan[2]);
        int32_t ce = 0, found = 0;
        CALL(ebmc_shape_counts(cyl, NULL, &ce, NULL));
        for (int32_t i = 0; i < ce; ++i) {
            CALL(ebmc_edge_geom(cyl, i, &ct, mid, tan, &rad));
            if (ct == EBMC_CURVE_CIRCLE && fabs(rad - 2.5) < 1e-9) found++;
        }
        CHECK(found == 2, "cylinder has %d circular edges of radius 2.5", found);

        ebmc_shape *rot = NULL, *str = NULL, *cmp = NULL;
        const double rz[12] = { 0, -1, 0, 5,  1, 0, 0, 0,  0, 0, 1, 0 };   /* 90 deg about Z, then +5 in X */
        CALL(ebmc_transform(box, rz, &rot));
        double bb[6]; CALL(ebmc_bbox(rot, bb));
        CHECK(fabs(bb[0] + 15) < 1e-6 && fabs(bb[3] - 5) < 1e-6, "rotated box x range %g..%g", bb[0], bb[3]);
        const double sx[12] = { 2, 0, 0, 0,  0, 1, 0, 0,  0, 0, 1, 0 };    /* non-uniform scale */
        CALL(ebmc_transform(cyl, sx, &str));
        CALL(ebmc_bbox(str, bb));
        CALL(ebmc_volume(str, &v));
        expect = 2 * M_PI * 2.5 * 2.5 * 40;
        /* the stretched surfaces become B-splines; GProp's default integration on them is only good to ~1% */
        CHECK(fabs(bb[0] + 5) < 1e-3 && fabs(bb[3] - 5) < 1e-3 && fabs(bb[4] - 2.5) < 1e-3 && fabs(v - expect) / expect < 0.02,
              "stretched cylinder x %g..%g y max %g, volume %.3f (exact %.3f)", bb[0], bb[3], bb[4], v, expect);
        const ebmc_shape* parts[2] = { box, rot };
        CALL(ebmc_compound(parts, 2, &cmp));
        CALL(ebmc_shape_counts(cmp, &f, NULL, NULL));
        CHECK(f == 12, "compound of two boxes has %d faces", f);
        ebmc_shape_free(rot); ebmc_shape_free(str); ebmc_shape_free(cmp);
    }

    int rc = ebmc_fillet_edges(box, &edge0, 1, 25.0, &bad);
    CHECK(rc != EBMC_OK && bad == NULL, "fillet r=25 on a 20 mm box -> rc %d, '%s'", rc, ebmc_occt_last_error());
    int32_t badEdge = 99;
    rc = ebmc_fillet_edges(box, &badEdge, 1, 1.0, &bad);
    CHECK(rc == EBMC_ERR_INDEX, "edge 99 -> rc %d, '%s'", rc, ebmc_occt_last_error());

    ebmc_shape_free(box); ebmc_shape_free(fil); ebmc_shape_free(cyl); ebmc_shape_free(cylm);
    ebmc_shape_free(cut); ebmc_shape_free(rd);
    printf(failures ? "SMOKE TEST FAILED (%d)\n" : "SMOKE TEST PASSED\n", failures);
    return failures ? 1 : 0;
}
