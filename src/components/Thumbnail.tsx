import { Ionicons } from '@expo/vector-icons';
import { useEffect, useState } from 'react';
import { Image, StyleSheet, View, ViewStyle } from 'react-native';
import * as thumbnails from '../services/thumbnails';
import { theme } from '../theme';

interface Props {
  item: thumbnails.ThumbSource;
  style?: ViewStyle;
  iconSize?: number;
  radius?: number;
}

/** Video poster frame with a graceful film-icon placeholder while it loads. */
export function Thumbnail({ item, style, iconSize = 24, radius = 0 }: Props) {
  const [uri, setUri] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    setUri(null);
    thumbnails
      .getThumbnail(item)
      .then((u) => alive && setUri(u))
      .catch(() => {});
    return () => {
      alive = false;
    };
  }, [item.id, item.name, item.folderName]);

  return (
    <View style={[styles.base, { borderRadius: radius }, style]}>
      {uri ? (
        <Image source={{ uri }} style={[styles.image, { borderRadius: radius }]} resizeMode="cover" />
      ) : (
        <Ionicons name="film-outline" size={iconSize} color={theme.colors.textMuted} />
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  base: {
    backgroundColor: theme.colors.surfaceAlt,
    alignItems: 'center',
    justifyContent: 'center',
    overflow: 'hidden',
  },
  image: { width: '100%', height: '100%' },
});
