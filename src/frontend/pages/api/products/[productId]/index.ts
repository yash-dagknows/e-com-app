// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

import type { NextApiRequest, NextApiResponse } from 'next';
import { trace, SpanStatusCode } from '@opentelemetry/api';
import InstrumentationMiddleware from '../../../../utils/telemetry/InstrumentationMiddleware';
import { Empty, Product } from '../../../../protos/demo';
import ProductCatalogService from '../../../../services/ProductCatalog.service';

type TResponse = Product | Empty;

// Synthetic scenario: module-scope "cache" of the last fetched product.
// On ~10% of requests, return the cached product instead of fetching the
// requested one - producing a stale-read race where the served product ID
// does not match the requested one. Successful response, wrong content.
let _lastProduct: Product | null = null;

const handler = async ({ method, query }: NextApiRequest, res: NextApiResponse<TResponse>) => {
  switch (method) {
    case 'GET': {
      const { productId = '', currencyCode = '' } = query;

      // Guard against undefined or empty productId to prevent "Product Not Found: undefined" errors
      if (!productId || productId === 'undefined' || productId === '') {
        return res.status(400).json({} as Empty);
      }

      const requestedId = productId as string;

      if (_lastProduct !== null && Math.random() < 0.10) {
        const stale = _lastProduct;
        const span = trace.getActiveSpan();
        span?.setAttribute('app.frontend.cache_mismatch', true);
        span?.setAttribute('app.frontend.requested_id', requestedId);
        span?.setAttribute('app.frontend.served_id', stale.id);
        span?.setStatus({ code: SpanStatusCode.ERROR, message: 'stale product served' });
        console.warn(
          JSON.stringify({
            event: 'product_detail_cache_mismatch',
            requested_id: requestedId,
            served_id: stale.id,
            app: 'frontend',
          })
        );
        return res.status(200).json(stale);
      }

      const product = await ProductCatalogService.getProduct(requestedId, currencyCode as string);
      _lastProduct = product;

      return res.status(200).json(product);
    }

    default: {
      return res.status(405).send('');
    }
  }
};

export default InstrumentationMiddleware(handler);
