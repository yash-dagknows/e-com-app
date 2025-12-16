// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

import type { NextApiRequest, NextApiResponse } from 'next';
import InstrumentationMiddleware from '../../../../utils/telemetry/InstrumentationMiddleware';
import { Empty, Product } from '../../../../protos/demo';
import ProductCatalogService from '../../../../services/ProductCatalog.service';

type TResponse = Product | Empty;

const handler = async ({ method, query }: NextApiRequest, res: NextApiResponse<TResponse>) => {
  switch (method) {
    case 'GET': {
      const { productId = '', currencyCode = '' } = query;
      
      // Guard against undefined or empty productId to prevent "Product Not Found: undefined" errors
      if (!productId || productId === 'undefined' || productId === '') {
        return res.status(400).json({} as Empty);
      }
      
      const product = await ProductCatalogService.getProduct(productId as string, currencyCode as string);

      return res.status(200).json(product);
    }

    default: {
      return res.status(405).send('');
    }
  }
};

export default InstrumentationMiddleware(handler);
